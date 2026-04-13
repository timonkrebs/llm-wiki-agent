#!/usr/bin/dotnet run
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

var saveFlag = args.FirstOrDefault(a => a == "--save" || a.StartsWith("--save="));
string? savePath = null;
if (saveFlag != null)
{
    savePath = saveFlag.StartsWith("--save=") ? saveFlag.Substring(7) : "";
}

var questionArgs = args.Where(a => a != "--save" && !a.StartsWith("--save=")).ToList();
if (questionArgs.Count == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("    ./tools/query.cs \"What are the main themes across all sources?\"");
    Console.WriteLine("    ./tools/query.cs \"How does ConceptA relate to ConceptB?\" --save");
    Console.WriteLine("    ./tools/query.cs \"Summarize everything about EntityName\" --save=synthesis/my-analysis.md");
    return;
}
var question = string.Join(" ", questionArgs);

string GetRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string? path = null) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
var repoRoot = GetRepoRoot();
var wikiDir = Path.Combine(repoRoot, "wiki");
var indexFile = Path.Combine(wikiDir, "index.md");
var logFile = Path.Combine(wikiDir, "log.md");
var schemaFile = Path.Combine(repoRoot, "CLAUDE.md");

string ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

void WriteFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, Encoding.UTF8);
    Console.WriteLine($"  saved: {Path.GetRelativePath(repoRoot, path)}");
}

async Task<string> CallLlmAsync(string prompt, int maxTokens = 4096)
{
    var model = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gemma4:26b";
    using var client = new HttpClient();

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

    try
    {
        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error calling LLM: {ex.Message}");
        return "";
    }
}

List<string> FindRelevantPages(string q, string indexContent)
{
    var relevant = new List<string>();
    var qLower = q.ToLower();
    var mdLinks = Regex.Matches(indexContent, @"\[([^\]]+)\]\(([^)]+)\)");

    foreach (Match match in mdLinks)
    {
        var title = match.Groups[1].Value;
        var href = match.Groups[2].Value;
        var titleLower = title.ToLower();
        var isMatch = false;

        var words = titleLower.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.Length > 3 && qLower.Contains(w)))
        {
            isMatch = true;
        }
        else if (titleLower.Length >= 2 && qLower.Contains(titleLower))
        {
            isMatch = true;
        }
        else
        {
            var cjkMatches = Regex.Matches(titleLower, @"[^\x00-\x7F]{2,}");
            if (cjkMatches.Cast<Match>().Any(m => qLower.Contains(m.Value)))
            {
                isMatch = true;
            }
        }

        if (isMatch)
        {
            var p = Path.Combine(wikiDir, href);
            if (File.Exists(p) && !relevant.Contains(p))
            {
                relevant.Add(p);
            }
        }
    }

    var overview = Path.Combine(wikiDir, "overview.md");
    if (File.Exists(overview) && !relevant.Contains(overview))
    {
        relevant.Insert(0, overview);
    }

    return relevant.Take(12).ToList();
}

void AppendLog(string entry)
{
    var existing = ReadFile(logFile);
    File.WriteAllText(logFile, entry.Trim() + "\n\n" + existing, Encoding.UTF8);
}

var today = DateTime.Now.ToString("yyyy-MM-dd");
var indexContent = ReadFile(indexFile);
if (string.IsNullOrWhiteSpace(indexContent))
{
    Console.WriteLine("Wiki is empty. Ingest some sources first with: ./tools/ingest.cs <source>");
    return;
}

var relevantPages = FindRelevantPages(question, indexContent);

if (relevantPages.Count <= 1)
{
    Console.WriteLine("  selecting relevant pages via API...");
    var p = $"Given this wiki index:\n\n{indexContent}\n\nWhich pages are most relevant to answering: \"{question}\"\n\nReturn ONLY a JSON array of relative file paths (as listed in the index), e.g. [\"sources/foo.md\", \"concepts/Bar.md\"]. Maximum 10 pages.";
    var raw = await CallLlmAsync(p, 512);
    raw = raw.Trim();
    raw = Regex.Replace(raw, @"^```(?:json)?\s*", "");
    raw = Regex.Replace(raw, @"\s*```$", "");
    try
    {
        var paths = JsonSerializer.Deserialize<List<string>>(raw);
        if (paths != null)
        {
            foreach (var path in paths)
            {
                var fullPath = Path.Combine(wikiDir, path);
                if (File.Exists(fullPath) && !relevantPages.Contains(fullPath))
                {
                    relevantPages.Add(fullPath);
                }
            }
        }
    }
    catch
    {
        // ignore
    }
}

var pagesContext = new StringBuilder();
foreach (var rp in relevantPages)
{
    var rel = Path.GetRelativePath(repoRoot, rp);
    pagesContext.Append($"\n\n### {rel}\n{File.ReadAllText(rp)}");
}

if (pagesContext.Length == 0)
{
    pagesContext.Append($"\n\n### wiki/index.md\n{indexContent}");
}

var schema = ReadFile(schemaFile);

Console.WriteLine($"  synthesizing answer from {relevantPages.Count} pages...");
var promptStr = $@"You are querying an LLM Wiki to answer a question. Use the wiki pages below to synthesize a thorough answer. Cite sources using [[PageName]] wikilink syntax.

Schema:
{schema}

Wiki pages:
{pagesContext}

Question: {question}

Write a well-structured markdown answer with headers, bullets, and [[wikilink]] citations. At the end, add a ## Sources section listing the pages you drew from.
";

var answer = await CallLlmAsync(promptStr, 4096);
Console.WriteLine("\n" + new string('=', 60));
Console.WriteLine(answer);
Console.WriteLine(new string('=', 60));

if (savePath != null)
{
    if (string.IsNullOrEmpty(savePath))
    {
        Console.Write("\nSave as (slug, e.g. 'my-analysis'): ");
        var slug = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(slug))
        {
            Console.WriteLine("Skipping save.");
        }
        else
        {
            savePath = $"syntheses/{slug}.md";
        }
    }

    if (!string.IsNullOrEmpty(savePath))
    {
        var fullSavePath = Path.Combine(wikiDir, savePath);
        var frontmatter = $@"---
title: ""{question.Substring(0, Math.Min(question.Length, 80))}""
type: synthesis
tags: []
sources: []
last_updated: {today}
---

";
        WriteFile(fullSavePath, frontmatter + answer);

        indexContent = ReadFile(indexFile);
        var entry = $"- [{question.Substring(0, Math.Min(question.Length, 60))}]({savePath}) — synthesis";
        if (indexContent.Contains("## Syntheses"))
        {
            indexContent = indexContent.Replace("## Syntheses\n", $"## Syntheses\n{entry}\n");
            File.WriteAllText(indexFile, indexContent, Encoding.UTF8);
        }
        Console.WriteLine($"  indexed: {savePath}");
    }
}

var logMsg = $"## [{today}] query | {question.Substring(0, Math.Min(question.Length, 80))}\n\nSynthesized answer from {relevantPages.Count} pages.";
if (!string.IsNullOrEmpty(savePath)) logMsg += $" Saved to {savePath}.";
AppendLog(logMsg);
