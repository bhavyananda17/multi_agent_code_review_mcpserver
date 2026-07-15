using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Tools;

public class CodeAnalysisTool : ICodeAnalysisTool
{
    public async Task<int> GetCyclomaticComplexityAsync(string filePath, string methodName, string basePath = "")
    {
        var fullPath = ResolvePath(filePath, basePath);
        var syntaxTree = await GetSyntaxTreeAsync(fullPath);
        var method = FindMethod(syntaxTree, methodName);
        if (method == null) return 0;
        return CalculateCyclomaticComplexity(method);
    }

    public async Task<DependencyGraph> GetDependencyGraphAsync(string filePath, string basePath = "")
    {
        var fullPath = ResolvePath(filePath, basePath);
        
        if (!File.Exists(fullPath))
        {
            return new DependencyGraph(new Dictionary<string, List<string>>(), new Dictionary<string, List<string>>(), new List<string>());
        }
        
        var syntaxTree = await GetSyntaxTreeAsync(fullPath);
        var root = await syntaxTree.GetRootAsync();
        
        var fileDependencies = new Dictionary<string, List<string>>();
        var reverseDependencies = new Dictionary<string, List<string>>();
        
        var fileName = Path.GetFileName(filePath);
        fileDependencies[fileName] = new List<string>();
        
        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name.ToString())
            .Where(u => !u.StartsWith("System.") && !u.StartsWith("Microsoft."))
            .Distinct()
            .ToList();
            
        foreach (var ns in usings)
        {
            var depName = ns.Split('.').Last();
            fileDependencies[fileName].Add(depName);
            
            if (!reverseDependencies.ContainsKey(depName))
                reverseDependencies[depName] = new();
            reverseDependencies[depName].Add(fileName);
        }

        var typeReferences = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.Text)
            .Where(t => char.IsUpper(t[0]) && t != fileName.Replace(".cs", ""))
            .Distinct()
            .ToList();
            
        foreach (var typeRef in typeReferences)
        {
            if (!fileDependencies[fileName].Contains(typeRef))
                fileDependencies[fileName].Add(typeRef);
        }

        return new DependencyGraph(fileDependencies, reverseDependencies, fileDependencies.Keys.ToList());
    }

    public async Task<List<CallSite>> FindCallersAsync(string filePath, string methodName, string basePath = "")
    {
        var fullPath = ResolvePath(filePath, basePath);
        var syntaxTree = await GetSyntaxTreeAsync(fullPath);
        var root = await syntaxTree.GetRootAsync();
        var semanticModel = (await GetCompilationAsync(fullPath)).GetSemanticModel(syntaxTree);
        
        var callers = new List<CallSite>();
        
        foreach (var node in syntaxTree.GetRoot().DescendantNodes())
        {
            if (node is InvocationExpressionSyntax invocation)
            {
                var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol?.Name == methodName)
                {
                    var containingMethod = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                    if (containingMethod != null)
                    {
                        callers.Add(new CallSite
                        (
                            filePath,
                            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            methodName,
                            containingMethod.Identifier.Text
                        ));
                    }
                }
            }
        }

        return callers;
    }

    public async Task<List<CodeSmell>> DetectCodeSmellsAsync(string filePath, string basePath = "")
    {
        var fullPath = ResolvePath(filePath, basePath);
        var syntaxTree = await GetSyntaxTreeAsync(fullPath);
        var compilation = await GetCompilationAsync(fullPath);
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var smells = new List<CodeSmell>();

        // Long methods
        foreach (var method in syntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var lines = method.GetLocation().GetLineSpan();
            var lineCount = lines.EndLinePosition.Line - lines.StartLinePosition.Line + 1;
            if (lineCount > 50)
            {
                smells.Add(new CodeSmell
                (
                    "LongMethod",
                    $"Method '{method.Identifier}' has {lineCount} lines (max recommended: 50)",
                    filePath,
                    method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Severity.Medium
                ));
            }
        }

        // Large classes
        foreach (var classDecl in syntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var memberCount = classDecl.Members.Count;
            if (memberCount > 20)
            {
                smells.Add(new CodeSmell
                (
                    "LargeClass",
                    $"Class '{classDecl.Identifier}' has {memberCount} members (max recommended: 20)",
                    filePath,
                    classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Severity.Medium
                ));
            }
        }

        // Duplicated code
        var methodBodies = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body != null)
            .ToList();

        for (int i = 0; i < methodBodies.Count; i++)
        {
            for (int j = i + 1; j < methodBodies.Count; j++)
            {
                var similarity = CalculateSimilarity(methodBodies[i].Body!.ToString(), methodBodies[j].Body!.ToString());
                if (similarity > 0.8)
                {
                    smells.Add(new CodeSmell
                    (
                        "DuplicatedCode",
                        $"Methods '{methodBodies[i].Identifier}' and '{methodBodies[j].Identifier}' have {similarity:P0} similarity",
                        filePath,
                        methodBodies[i].GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Severity.High
                    ));
                }
            }
        }

        // Unused usings
        var unusedUsings = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Where(u => !IsUsingUsed(syntaxTree, u))
            .ToList();

        foreach (var unused in unusedUsings)
        {
            smells.Add(new CodeSmell
            (
                "UnusedUsing",
                $"Unused using directive: {unused.Name}",
                filePath,
                unused.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Severity.Low
            ));
        }

        // Async void methods
        foreach (var method in syntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.ReturnType is PredefinedTypeSyntax pts && pts.Keyword.IsKind(SyntaxKind.VoidKeyword) &&
                method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                smells.Add(new CodeSmell
                (
                    "AsyncVoid",
                    $"Async void method '{method.Identifier}' should return Task",
                    filePath,
                    method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Severity.High
                ));
            }
        }

        return smells;
    }

    private string ResolvePath(string filePath, string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return filePath;
        
        if (Path.IsPathRooted(filePath))
            return filePath;
            
        return Path.Combine(basePath, filePath);
    }

    private async Task<SyntaxTree> GetSyntaxTreeAsync(string filePath)
    {
        var text = await File.ReadAllTextAsync(filePath);
        return CSharpSyntaxTree.ParseText(text);
    }

    private async Task<CSharpCompilation> GetCompilationAsync(string filePath)
    {
        var syntaxTree = await GetSyntaxTreeAsync(filePath);
        return CSharpCompilation.Create("temp")
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddReferences(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));
    }

    private MethodDeclarationSyntax? FindMethod(SyntaxTree syntaxTree, string methodName)
    {
        return syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
    }

    private int CalculateCyclomaticComplexity(MethodDeclarationSyntax method)
    {
        var complexity = 1; // Base complexity
        
        foreach (var node in method.DescendantNodes())
        {
            switch (node.Kind())
            {
                case SyntaxKind.IfStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForEachStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.CatchClause:
                case SyntaxKind.ConditionalExpression:
                case SyntaxKind.LogicalAndExpression:
                case SyntaxKind.LogicalOrExpression:
                    complexity++;
                    break;
                case SyntaxKind.SwitchExpression:
                    var switchExprNode = (SwitchExpressionSyntax)node;
                    complexity += switchExprNode.Arms.Count;
                    break;
                case SyntaxKind.SwitchStatement:
                    var switchStmtNode = (SwitchStatementSyntax)node;
                    complexity += switchStmtNode.Sections.Count;
                    break;
            }
        }

        return complexity;
    }

    private double CalculateSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2)) return 0;
        
        var set1 = new HashSet<string>(str1.Split(new[] { ' ', '\t', '\n', '\r', '{', '}', '(', ')', ';', ',' }, StringSplitOptions.RemoveEmptyEntries));
        var set2 = new HashSet<string>(str2.Split(new[] { ' ', '\t', '\n', '\r', '{', '}', '(', ')', ';', ',' }, StringSplitOptions.RemoveEmptyEntries));
        
        var intersection = set1.Intersect(set2).Count();
        var union = set1.Union(set2).Count();
        
        return union == 0 ? 0 : (double)intersection / union;
    }

    private bool IsUsingUsed(SyntaxTree syntaxTree, UsingDirectiveSyntax usingDirective)
    {
        var root = syntaxTree.GetRoot();
        var namespaceName = usingDirective.Name.ToString();
        
        return root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Any(id => id.Identifier.ValueText == namespaceName.Split('.').Last() ||
                       (id.Parent is QualifiedNameSyntax qns && qns.ToString().StartsWith(namespaceName)));
    }
}