#!/usr/bin/dotnet run
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

if (args.Length == 0)
{
    Console.WriteLine("Usage: ./tools/ingest.cs <path-to-source> [path2 ...] [dir1 ...]");
    return;
}

string GetRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string? path = null) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
var repoRoot = GetRepoRoot();
var wikiDir = Path.Combine(repoRoot, "wiki");
var logFile = Path.Combine(wikiDir, "log.md");
var indexFile = Path.Combine(wikiDir, "index.md");
var overviewFile = Path.Combine(wikiDir, "overview.md");
var schemaFile = Path.Combine(repoRoot, "CLAUDE.md");

string Sha256(string text)
{
    using var sha256 = SHA256.Create();
    var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
    var builder = new StringBuilder();
    for (int i = 0; i < bytes.Length; i++)
    {
        builder.Append(bytes[i].ToString("x2"));
    }
    return builder.ToString().Substring(0, 16);
}

string ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

void WriteFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, Encoding.UTF8);
    Console.WriteLine($"  wrote: {Path.GetRelativePath(repoRoot, path)}");
}

async Task<string> CallLlmAsync(string prompt, int maxTokens = 8192)
{
    var model = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gemma4:26b";
    using var client = new HttpClient();
    // increase timeout for ingest since it reads huge files and returns huge JSON
    client.Timeout = TimeSpan.FromMinutes(10);

    var requestData = new
    {
        model = model,
        messages = new[] { new { role = "user", content = prompt } },
        max_tokens = maxTokens,
        stream = false
    };

    var json = JsonSerializer.Serialize(requestData);
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    var url = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434/v1/chat/completions";

    var response = await client.PostAsync(url, content);
    response.EnsureSuccessStatusCode();
    var responseJson = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(responseJson);
    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
}

string BuildWikiContext()
{
    var parts = new List<string>();
    if (File.Exists(indexFile)) parts.Add($"## wiki/index.md\n{ReadFile(indexFile)}");
    if (File.Exists(overviewFile)) parts.Add($"## wiki/overview.md\n{ReadFile(overviewFile)}");

    var sourcesDir = Path.Combine(wikiDir, "sources");
    if (Directory.Exists(sourcesDir))
    {
        var recent = new DirectoryInfo(sourcesDir).GetFiles("*.md")
            .OrderByDescending(f => f.LastWriteTime)
            .Take(5);
        foreach (var p in recent)
        {
            parts.Add($"## {Path.GetRelativePath(repoRoot, p.FullName)}\n{File.ReadAllText(p.FullName)}");
        }
    }
    return string.Join("\n\n---\n\n", parts);
}

JsonDocument ParseJsonFromResponse(string text)
{
    text = Regex.Replace(text.Trim(), @"^```(?:json)?\s*", "");
    text = Regex.Replace(text, @"\s*```$", "");
    var match = Regex.Match(text, @"\{[\s\S]*\}");
    if (!match.Success) throw new Exception("No JSON object found in response");
    return JsonDocument.Parse(match.Value);
}

void UpdateIndex(string newEntry, string section = "Sources")
{
    var content = ReadFile(indexFile);
    if (string.IsNullOrEmpty(content))
    {
        content = "# Wiki Index\n\n## Overview\n- [Overview](overview.md) — living synthesis\n\n## Sources\n\n## Entities\n\n## Concepts\n\n## Syntheses\n";
    }
    var sectionHeader = $"## {section}";
    if (content.Contains(sectionHeader))
    {
        content = content.Replace(sectionHeader + "\n", sectionHeader + "\n" + newEntry + "\n");
    }
    else
    {
        content += $"\n{sectionHeader}\n{newEntry}\n";
    }
    WriteFile(indexFile, content);
}

void AppendLog(string entry)
{
    var existing = ReadFile(logFile);
    WriteFile(logFile, entry.Trim() + "\n\n" + existing);
}

async Task IngestAsync(string sourcePath)
{
    var source = Path.GetFullPath(sourcePath);
    if (!File.Exists(source))
    {
        Console.WriteLine($"Error: file not found: {sourcePath}");
        Environment.Exit(1);
    }

    var sourceContent = File.ReadAllText(source);
    var sourceHash = Sha256(sourceContent);
    var today = DateTime.Now.ToString("yyyy-MM-dd");

    Console.WriteLine($"\nIngesting: {Path.GetFileName(source)}  (hash: {sourceHash})");

    var wikiContext = BuildWikiContext();
    var schema = ReadFile(schemaFile);

    var prompt = $@"You are maintaining an LLM Wiki. Process this source document and integrate its knowledge into the wiki.

Schema and conventions:
{schema}

Current wiki state (index + recent pages):
{(string.IsNullOrEmpty(wikiContext) ? "(wiki is empty — this is the first source)" : wikiContext)}

New source to ingest (file: {Path.GetRelativePath(repoRoot, source)}):
=== SOURCE START ===
{sourceContent}
=== SOURCE END ===

Today's date: {today}

Return ONLY a valid JSON object with these fields (no markdown fences, no prose outside the JSON):
{{
  ""title"": ""Human-readable title for this source"",
  ""slug"": ""kebab-case-slug-for-filename"",
  ""source_page"": ""full markdown content for wiki/sources/<slug>.md — use the source page format from the schema"",
  ""index_entry"": ""- [Title](sources/slug.md) — one-line summary"",
  ""overview_update"": ""full updated content for wiki/overview.md, or null if no update needed"",
  ""entity_pages"": [
    {{""path"": ""entities/EntityName.md"", ""content"": ""full markdown content""}}
  ],
  ""concept_pages"": [
    {{""path"": ""concepts/ConceptName.md"", ""content"": ""full markdown content""}}
  ],
  ""contradictions"": [""describe any contradiction with existing wiki content, or empty list""],
  ""log_entry"": ""## [{today}] ingest | <title>\n\nAdded source. Key claims: ...""
}}
";

    Console.WriteLine("  calling API (model: gemma4:26b)...");
    var raw = "";
    try
    {
        raw = await CallLlmAsync(prompt, 8192);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error calling API: {ex.Message}");
        Environment.Exit(1);
    }

    JsonDocument data;
    try
    {
        data = ParseJsonFromResponse(raw);
    }
    catch (Exception e)
    {
        Console.WriteLine($"Error parsing API response: {e.Message}");
        Console.WriteLine("Raw response saved to /tmp/ingest_debug.txt");
        File.WriteAllText("/tmp/ingest_debug.txt", raw);
        Environment.Exit(1);
        return;
    }

    var root = data.RootElement;
    var slug = root.GetProperty("slug").GetString()!;
    WriteFile(Path.Combine(wikiDir, "sources", $"{slug}.md"), root.GetProperty("source_page").GetString()!);

    if (root.TryGetProperty("entity_pages", out var entityPages) && entityPages.ValueKind == JsonValueKind.Array)
    {
        foreach (var page in entityPages.EnumerateArray())
        {
            WriteFile(Path.Combine(wikiDir, page.GetProperty("path").GetString()!), page.GetProperty("content").GetString()!);
        }
    }

    if (root.TryGetProperty("concept_pages", out var conceptPages) && conceptPages.ValueKind == JsonValueKind.Array)
    {
        foreach (var page in conceptPages.EnumerateArray())
        {
            WriteFile(Path.Combine(wikiDir, page.GetProperty("path").GetString()!), page.GetProperty("content").GetString()!);
        }
    }

    if (root.TryGetProperty("overview_update", out var overviewUpdate) && overviewUpdate.ValueKind == JsonValueKind.String)
    {
        var content = overviewUpdate.GetString();
        if (!string.IsNullOrEmpty(content) && content != "null")
        {
            WriteFile(overviewFile, content);
        }
    }

    UpdateIndex(root.GetProperty("index_entry").GetString()!, "Sources");
    AppendLog(root.GetProperty("log_entry").GetString()!);

    if (root.TryGetProperty("contradictions", out var contradictions) && contradictions.ValueKind == JsonValueKind.Array)
    {
        var count = contradictions.GetArrayLength();
        if (count > 0)
        {
            Console.WriteLine("\n  ⚠️  Contradictions detected:");
            foreach (var c in contradictions.EnumerateArray())
            {
                Console.WriteLine($"     - {c.GetString()}");
            }
        }
    }

    Console.WriteLine($"\nDone. Ingested: {root.GetProperty("title").GetString()}");
}

var pathsToProcess = new List<string>();
foreach (var arg in args)
{
    if (File.Exists(arg) && arg.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    {
        pathsToProcess.Add(Path.GetFullPath(arg));
    }
    else if (Directory.Exists(arg))
    {
        pathsToProcess.AddRange(Directory.GetFiles(arg, "*.md", SearchOption.AllDirectories).Select(Path.GetFullPath));
    }
}

var uniquePaths = pathsToProcess.Distinct().ToList();
if (uniquePaths.Count == 0)
{
    Console.WriteLine("Error: no markdown files found to ingest.");
    return;
}

if (uniquePaths.Count > 1)
{
    Console.WriteLine($"Batch mode: found {uniquePaths.Count} files to ingest.");
}

foreach (var p in uniquePaths)
{
    await IngestAsync(p);
}
