// Extracts every method/constructor from Necroking/**/*.cs into:
//  - catalog.json  (metadata, no bodies) for clustering
//  - batches/batch_NNN.json (with bodies) for LLM labeling agents
// Usage: MethodExtractor <repoRoot> <outDir>
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 2) { Console.Error.WriteLine("usage: MethodExtractor <repoRoot> <outDir>"); return 2; }
string repoRoot = Path.GetFullPath(args[0]);
string outDir = Path.GetFullPath(args[1]);
string srcRoot = Path.Combine(repoRoot, "Necroking");
Directory.CreateDirectory(outDir);
Directory.CreateDirectory(Path.Combine(outDir, "batches"));

var records = new List<MethodRec>();
int id = 0;
foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
{
    string rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
    if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
    string text = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(text);
    var root = tree.GetRoot();
    foreach (var node in root.DescendantNodes())
    {
        string kind; string name; SyntaxNode decl = node;
        switch (node)
        {
            case MethodDeclarationSyntax m:
                if (m.Body == null && m.ExpressionBody == null) continue; // abstract/partial stubs
                kind = "method"; name = m.Identifier.Text; break;
            case ConstructorDeclarationSyntax c:
                if (c.Body == null && c.ExpressionBody == null) continue;
                kind = "ctor"; name = c.Identifier.Text; break;
            default: continue;
        }
        var span = tree.GetLineSpan(node.Span);
        int startLine = span.StartLinePosition.Line + 1;
        int endLine = span.EndLinePosition.Line + 1;
        string typeName = GetTypeName(node);
        string sig = GetSignature(node);
        string doc = GetDocComment(node);
        string body = node.ToString();
        records.Add(new MethodRec(id++, rel, typeName, name, kind, sig, startLine, endLine, endLine - startLine + 1, doc, body));
    }
}

Console.WriteLine($"Extracted {records.Count} methods/ctors from {records.Select(r => r.File).Distinct().Count()} files.");

var jsonOpts = new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

// catalog: no bodies, but carries the identity hashes (BodyHash/Key) the label store keys on.
// Normalization MUST match docs/consolidation-review/store/meta.json and tools/label_store.py:
//   Key     = file::type::name::sha1(sig_no_whitespace)[:8]
//   BodyHash= sha1( body, block+line comments stripped, ALL whitespace stripped )[:12]
var catalog = records.Select(r => new {
    r.Id, r.File, r.Type, r.Name, r.Kind, r.Sig, r.StartLine, r.EndLine, r.Lines, r.Doc,
    BodyHash = Body12(r.Body),
    Key = ComposeKey(r.File, r.Type, r.Name, r.Sig)
}).ToList();
File.WriteAllText(Path.Combine(outDir, "catalog.json"), JsonSerializer.Serialize(catalog, jsonOpts));

// Auto-labeled scenario boilerplate: skip from agent batches
bool IsAutoScenario(MethodRec r) => r.File.StartsWith("Necroking/Scenario/Scenarios/");
var auto = records.Where(IsAutoScenario).ToList();
File.WriteAllText(Path.Combine(outDir, "auto_scenarios.json"),
    JsonSerializer.Serialize(auto.Select(r => new { r.Id, r.File, r.Type, r.Name }), jsonOpts));

// batches with bodies (~150k chars each), grouping same-file methods together
var toLabel = records.Where(r => !IsAutoScenario(r)).ToList();
const int BatchBudget = 150_000;
int batchNum = 0; long acc = 0;
var cur = new List<MethodRec>();
void Flush()
{
    if (cur.Count == 0) return;
    string path = Path.Combine(outDir, "batches", $"batch_{batchNum:D3}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(cur, jsonOpts));
    batchNum++; acc = 0; cur = new List<MethodRec>();
}
foreach (var group in toLabel.GroupBy(r => r.File))
{
    long groupSize = group.Sum(r => (long)r.Body.Length);
    if (acc > 0 && acc + groupSize > BatchBudget) Flush();
    foreach (var r in group)
    {
        cur.Add(r);
        acc += r.Body.Length;
        if (acc > BatchBudget * 3 / 2) Flush(); // giant files: split mid-file
    }
}
Flush();
Console.WriteLine($"Wrote {batchNum} batches for {toLabel.Count} methods (auto-labeled {auto.Count} scenario methods).");
return 0;

static string GetTypeName(SyntaxNode node)
{
    var parts = new List<string>();
    for (var p = node.Parent; p != null; p = p.Parent)
        if (p is BaseTypeDeclarationSyntax t) parts.Insert(0, t.Identifier.Text);
    return parts.Count > 0 ? string.Join(".", parts) : "<global>";
}

static string GetSignature(SyntaxNode node)
{
    switch (node)
    {
        case MethodDeclarationSyntax m:
            return $"{m.Modifiers} {m.ReturnType} {m.Identifier}{m.TypeParameterList}{m.ParameterList}".Trim();
        case ConstructorDeclarationSyntax c:
            return $"{c.Modifiers} {c.Identifier}{c.ParameterList}".Trim();
        default: return "";
    }
}

static string GetDocComment(SyntaxNode node)
{
    var sb = new StringBuilder();
    foreach (var trivia in node.GetLeadingTrivia())
    {
        if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
            sb.AppendLine(trivia.ToString().Trim());
    }
    string s = sb.ToString().Trim();
    return s.Length > 500 ? s[..500] : s;
}

// --- identity hashes (mirror of store/meta.json + tools/label_store.py) ---
static string Sha1Hex(string s)
{
    byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(s));
    var sb = new StringBuilder(h.Length * 2);
    foreach (byte b in h) sb.Append(b.ToString("x2"));
    return sb.ToString();
}

static string Body12(string body)
{
    string s = Regex.Replace(body, @"/\*.*?\*/", "", RegexOptions.Singleline); // block comments
    s = Regex.Replace(s, @"//[^\n]*", "");                                        // line + /// doc comments
    s = Regex.Replace(s, @"\s+", "");                                             // all whitespace
    return Sha1Hex(s)[..12];
}

static string ComposeKey(string file, string type, string name, string sig)
    => $"{file}::{type}::{name}::{Sha1Hex(Regex.Replace(sig, @"\s+", ""))[..8]}";

record MethodRec(int Id, string File, string Type, string Name, string Kind, string Sig,
    int StartLine, int EndLine, int Lines, string Doc, string Body);
