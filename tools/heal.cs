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

string GetRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string? path = null) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
var repoRoot = GetRepoRoot();
var wikiDir = Path.Combine(repoRoot, "wiki");
var entitiesDir = Path.Combine(wikiDir, "entities");

string ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

async Task<string> CallLlmAsync(string prompt, int maxTokens = 1500)
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

    var response = await client.PostAsync(url, content);
    response.EnsureSuccessStatusCode();
    var responseJson = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(responseJson);
    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
}

List<string> AllWikiPages()
{
    if (!Directory.Exists(wikiDir)) return new List<string>();
    return Directory.GetFiles(wikiDir, "*.md", SearchOption.AllDirectories)
        .Where(p => {
            var name = Path.GetFileName(p);
            return name != "index.md" && name != "log.md" && name != "lint-report.md";
        }).ToList();
}

List<string> ExtractWikilinks(string content)
{
    var matches = Regex.Matches(content, @"\[\[([^\]]+)\]\]");
    return matches.Select(m => m.Groups[1].Value).ToList();
}

List<string> FindMissingEntities(List<string> pages)
{
    var mentionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var existingPages = new HashSet<string>(pages.Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);

    foreach (var p in pages)
    {
        var content = ReadFile(p);
        foreach (var link in ExtractWikilinks(content))
        {
            if (!existingPages.Contains(link))
            {
                if (!mentionCounts.ContainsKey(link)) mentionCounts[link] = 0;
                mentionCounts[link]++;
            }
        }
    }
    return mentionCounts.Where(kvp => kvp.Value >= 3).Select(kvp => kvp.Key).ToList();
}

List<string> SearchSources(string entity, List<string> pages)
{
    var sources = new List<string>();
    var entityLower = entity.ToLower();
    foreach (var p in pages)
    {
        var parent = Path.GetDirectoryName(p) ?? "";
        if (!parent.Contains("entities") && !parent.Contains("concepts"))
        {
            var content = ReadFile(p).ToLower();
            if (content.Contains(entityLower))
            {
                sources.Add(p);
            }
        }
    }
    return sources.Take(15).ToList();
}

var pages = AllWikiPages();
var missingEntities = FindMissingEntities(pages);

if (missingEntities.Count == 0)
{
    Console.WriteLine("Graph is fully connected. No missing entities found!");
    return;
}

Directory.CreateDirectory(entitiesDir);
Console.WriteLine($"Found {missingEntities.Count} missing entity nodes. Commencing auto-heal...");

foreach (var entity in missingEntities)
{
    Console.WriteLine($"Healing entity page for: {entity}");
    var sources = SearchSources(entity, pages);

    var context = new StringBuilder();
    foreach (var s in sources)
    {
        var content = ReadFile(s);
        if (content.Length > 800) content = content.Substring(0, 800);
        context.Append($"\n\n### {Path.GetFileName(s)}\n{content}");
    }

    var sourceNames = string.Join(", ", sources.Select(s => $"\"{Path.GetFileName(s)}\""));

    var prompt = $@"You are filling a data gap in the Personal LLM Wiki.
Create an Entity definition page for ""{entity}"".

Here is how the entity appears in the current sources:
{context}

Format:
---
title: ""{entity}""
type: entity
tags: []
sources: [{sourceNames}]
---

# {entity}

Write a comprehensive paragraph defining what `{entity}` means in the context of this wiki, its main significance, and any actions or associations related to it.
";

    try
    {
        var result = await CallLlmAsync(prompt);
        var outPath = Path.Combine(entitiesDir, $"{entity}.md");
        File.WriteAllText(outPath, result, Encoding.UTF8);
        Console.WriteLine($" -> Saved to {Path.GetRelativePath(repoRoot, outPath)}");
    }
    catch (Exception e)
    {
        Console.WriteLine($" [!] Failed to generate {entity}: {e.Message}");
    }
}
