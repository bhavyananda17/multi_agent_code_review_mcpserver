#!/usr/bin/env python3
"""
Find merged GitHub PRs that make a usable benchmark row for `bench`, and build the CSV.

Ground truth = the first comment of each review thread that
  - is on a .cs file and is still applicable to the merged code (not "outdated"),
  - was written by a human other than the PR author (no bots),
  - is at least 40 characters long after removing code blocks, links and HTML comments.
PRs are kept only if they were squash/rebase-merged (the merge commit has one parent, so `<sha>~1..<sha>` is exactly the
PR), touch at least one .cs file, and are small (<= 10 files, <= 600 changed lines).

Needs the GitHub CLI (`gh auth login`) and only the Python standard library. All calls are read-only.

  python3 select_prs.py find --repos owner/repo [owner/repo ...] [--pages 3] --out candidates.json
  python3 select_prs.py csv  --candidates candidates.json --pick 1,3,4 --out real-prs.csv

Choosing which candidates to `--pick` is a human judgement (read the printed comments and keep PRs whose comments are
about the code); that choice is a source of selection bias and should be reported with the results.
"""
import argparse, csv, json, re, subprocess, sys

QUERY = """
query($owner:String!, $name:String!, $before:String) {
  repository(owner:$owner, name:$name) {
    pullRequests(states: MERGED, last: 50, before:$before, orderBy:{field:UPDATED_AT, direction:ASC}) {
      pageInfo { hasPreviousPage startCursor }
      nodes {
        number title url additions deletions changedFiles
        author { login }
        mergeCommit { oid parents(first:3){ totalCount } }
        files(first: 20) { nodes { path } }
        reviewThreads(first: 30) { nodes {
          isOutdated
          comments(first: 1) { nodes { author { login __typename } body path } }
        } }
      }
    }
  }
}
"""


def graphql(owner, name, pages):
    nodes, before = [], None
    for _ in range(pages):
        cmd = ["gh", "api", "graphql", "-f", f"query={QUERY}", "-F", f"owner={owner}", "-F", f"name={name}"]
        if before:
            cmd += ["-F", f"before={before}"]
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
        if out.returncode != 0 or not out.stdout.strip():
            print(f"  {owner}/{name}: query failed ({out.stderr.strip()[:120]})", file=sys.stderr)
            break
        page = json.loads(out.stdout)["data"]["repository"]["pullRequests"]
        nodes += page["nodes"]
        if not page["pageInfo"]["hasPreviousPage"]:
            break
        before = page["pageInfo"]["startCursor"]
    return nodes


def clean(body):
    body = re.sub(r"```.*?```", " ", body, flags=re.S)       # code and suggestion blocks
    body = re.sub(r"<!--.*?-->", " ", body, flags=re.S)
    body = re.sub(r"https?://\S+", " ", body)
    body = re.sub(r"@[\w-]+", " ", body)                      # @mentions
    return re.sub(r"\s+", " ", body).strip()


def find(repos, pages):
    candidates = []
    for repo in repos:
        owner, name = repo.split("/")
        for pr in graphql(owner, name, pages):
            commit = pr["mergeCommit"]
            if not commit or commit["parents"]["totalCount"] != 1:
                continue
            paths = [f["path"] for f in pr["files"]["nodes"]]
            if not any(p.endswith(".cs") for p in paths) or pr["changedFiles"] > 10 or pr["additions"] + pr["deletions"] > 600:
                continue
            author = (pr["author"] or {}).get("login")
            truth = []
            for thread in pr["reviewThreads"]["nodes"]:
                first = thread["comments"]["nodes"][0] if thread["comments"]["nodes"] else None
                if not first or thread["isOutdated"] or not (first["path"] or "").endswith(".cs"):
                    continue
                who = first["author"] or {}
                if who.get("__typename") != "User" or who.get("login") == author:
                    continue
                text = clean(first["body"])
                if len(text) >= 40:
                    truth.append({"path": first["path"], "text": text[:400], "by": who.get("login")})
            if len(truth) >= 2:
                candidates.append({"repo": repo, "number": pr["number"], "title": pr["title"], "url": pr["url"],
                                   "sha": commit["oid"], "files": len(paths), "lines": pr["additions"] + pr["deletions"], "truth": truth})
        print(f"{repo}: {sum(1 for c in candidates if c['repo'] == repo)} candidate(s)", file=sys.stderr)
    return candidates


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)
    f = sub.add_parser("find"); f.add_argument("--repos", nargs="+", required=True); f.add_argument("--pages", type=int, default=3); f.add_argument("--out", required=True)
    c = sub.add_parser("csv"); c.add_argument("--candidates", required=True); c.add_argument("--pick", required=True, help="comma-separated candidate indexes"); c.add_argument("--out", required=True)
    args = ap.parse_args()

    if args.command == "find":
        candidates = find(args.repos, args.pages)
        json.dump(candidates, open(args.out, "w"), indent=1)
        for i, x in enumerate(candidates):
            print(f"[{i}] {x['repo']}#{x['number']} files={x['files']} lines={x['lines']} threads={len(x['truth'])}  {x['title'][:70]}")
            for t in x["truth"][:4]:
                print(f"      - ({t['by']}) {t['text'][:140]}")
    else:
        candidates = json.load(open(args.candidates))
        with open(args.out, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh, quoting=csv.QUOTE_ALL)
            w.writerow(["repo_url", "commit_sha", "findings", "pr_url"])
            for i in (int(x) for x in args.pick.split(",")):
                x = candidates[i]
                w.writerow([f"https://github.com/{x['repo']}", x["sha"], json.dumps([t["text"] for t in x["truth"]], ensure_ascii=False), x["url"]])
        print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
