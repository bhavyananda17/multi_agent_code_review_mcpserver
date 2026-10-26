using MultiAgentCodeReview.Orchestration.Benchmarking;

namespace MultiAgentCodeReview.Tests;

public class BenchmarkCsvTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Reader_handles_quotes_escapes_embedded_newlines_and_blank_lines()
    {
        var records = BenchmarkCsv.ParseRecords("a,b,c\r\n\"x, y\",\"he said \"\"hi\"\"\",\"line1\nline2\"\n\n,,\nlast,row,here");
        Assert.Equal(4, records.Count);
        Assert.Equal("x, y", records[1].Fields[0]);
        Assert.Equal("he said \"hi\"", records[1].Fields[1]);
        Assert.Equal("line1\nline2", records[1].Fields[2]);
        Assert.Equal(new[] { 1, 2, 4, 6 }.Length, records.Count);
        Assert.Equal(2, records[1].Line);
    }

    [Fact]
    public void Reader_strips_a_byte_order_mark() =>
        Assert.Equal("a", BenchmarkCsv.ParseRecords("﻿a,b")[0].Fields[0]);

    [Fact]
    public void Reader_rejects_an_unterminated_quote_with_the_line_number()
    {
        var ex = Assert.Throws<FormatException>(() => BenchmarkCsv.ParseRecords("a,b\n\"oops,c"));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Reader_rejects_junk_after_a_closing_quote() =>
        Assert.Throws<FormatException>(() => BenchmarkCsv.ParseRecords("\"a\"x,b"));

    [Fact]
    public void Header_columns_can_be_in_any_order()
    {
        var rows = BenchmarkCsv.ParseRows($"findings,repo_url,commit_sha\n\"SQL injection | missing null check\",https://github.com/a/b,{Sha}\n");
        var row = Assert.Single(rows);
        Assert.Equal("https://github.com/a/b", row.RepoUrl);
        Assert.Equal(Sha, row.CommitSha);
        Assert.Equal(2, row.GroundTruth.Count);
    }

    [Fact]
    public void Header_names_are_matched_loosely() =>
        Assert.Single(BenchmarkCsv.ParseRows("Repository URL,Commit,Ground Truth\ngit@github.com:a/b.git,abcdef0,one\n"));

    [Fact]
    public void Without_a_header_columns_are_positional_and_json_arrays_are_understood()
    {
        var rows = BenchmarkCsv.ParseRows($"https://github.com/a/b,{Sha},\"[\"\"JSON one\"\", \"\"JSON, two\"\"]\"\nhttps://github.com/c/d,abcdef0,plain");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "JSON one", "JSON, two" }, rows[0].GroundTruth);
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.RowNumber));
    }

    [Fact]
    public void Findings_cell_splits_on_pipes_and_newlines_trims_and_deduplicates() =>
        Assert.Equal(new[] { "a", "b", "c" }, BenchmarkCsv.ParseFindingsCell(" a \n| b |A\n\n c"));

    [Fact]
    public void A_cell_that_merely_starts_with_a_bracket_is_treated_as_text() =>
        Assert.Equal(new[] { "[nit] rename this", "other" }, BenchmarkCsv.ParseFindingsCell("[nit] rename this | other"));

    [Fact]
    public void Extra_columns_are_ignored() =>
        Assert.Single(BenchmarkCsv.ParseRows($"https://github.com/a/b,{Sha},x,extra,more"));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("repo_url,commit_sha,findings\n", "no data rows")]
    [InlineData("repo_url,commit_sha\nhttps://x/y,abc1234", "header")]
    public void Unusable_files_are_rejected_with_a_reason(string csv, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => BenchmarkCsv.ParseRows(csv));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_bad_row_is_reported_up_front_with_its_line_number()
    {
        var ex = Assert.Throws<FormatException>(() => BenchmarkCsv.ParseRows(
            $"https://github.com/a/b,{Sha},ok\n/local/path,{Sha},x\nhttps://github.com/a/b,--orphan,x\nhttps://github.com/a/b,{Sha},   \nhttps://github.com/a/b,{Sha}"));
        Assert.Contains("line 2", ex.Message); Assert.Contains("not a git URL", ex.Message);
        Assert.Contains("line 3", ex.Message); Assert.Contains("not a commit SHA", ex.Message);
        Assert.Contains("line 4", ex.Message); Assert.Contains("no ground-truth", ex.Message);
        Assert.Contains("line 5", ex.Message); Assert.Contains("columns", ex.Message);
    }
}
