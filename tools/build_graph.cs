#!/usr/bin/dotnet run
#:property PublishAot=false
#:package QuikGraph@2.5.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QuikGraph;

var argsList = args.ToList();
var noInfer = argsList.Contains("--no-infer");
var openBrowser = argsList.Contains("--open");

string GetRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string? path = null) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
var repoRoot = GetRepoRoot();
var wikiDir = Path.Combine(repoRoot, "wiki");
var graphDir = Path.Combine(repoRoot, "graph");
var graphJson = Path.Combine(graphDir, "graph.json");
var graphHtml = Path.Combine(graphDir, "graph.html");
var cacheFile = Path.Combine(graphDir, ".cache.json");
var logFile = Path.Combine(wikiDir, "log.md");

var typeColors = new Dictionary<string, string>
{
    { "source", "#4CAF50" },
    { "entity", "#2196F3" },
    { "concept", "#FF9800" },
    { "synthesis", "#9C27B0" },
    { "unknown", "#9E9E9E" }
};

var edgeColors = new Dictionary<string, string>
{
    { "EXTRACTED", "#555555" },
    { "INFERRED", "#FF5722" },
    { "AMBIGUOUS", "#BDBDBD" }
};

var communityColors = new[]
{
    "#E91E63", "#00BCD4", "#8BC34A", "#FF5722", "#673AB7",
    "#FFC107", "#009688", "#F44336", "#3F51B5", "#CDDC39"
};

string ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

async Task<string> CallLlmAsync(string prompt, int maxTokens = 1024)
{
    var model = Environment.GetEnvironmentVariable("LLM_MODEL_FAST") ?? "gemma4:26b";
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

string Sha256(string text)
{
    using var sha256 = SHA256.Create();
    var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
    var builder = new StringBuilder();
    for (int i = 0; i < bytes.Length; i++)
    {
        builder.Append(bytes[i].ToString("x2"));
    }
    return builder.ToString();
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
    return matches.Select(m => m.Groups[1].Value).Distinct().ToList();
}

string ExtractFrontmatterType(string content)
{
    var match = Regex.Match(content, @"^type:\s*(\S+)", RegexOptions.Multiline);
    return match.Success ? match.Groups[1].Value.Trim('"', '\'') : "unknown";
}

string PageId(string path)
{
    return Path.GetRelativePath(wikiDir, path).Replace(".md", "").Replace("\\", "/");
}

Dictionary<string, JsonElement> LoadCache()
{
    if (File.Exists(cacheFile))
    {
        try
        {
            var text = File.ReadAllText(cacheFile);
            var doc = JsonDocument.Parse(text);
            var dict = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, JsonElement>();
        }
    }
    return new Dictionary<string, JsonElement>();
}

void SaveCache(Dictionary<string, object> cache)
{
    Directory.CreateDirectory(graphDir);
    var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(cacheFile, json);
}

List<Dictionary<string, object>> BuildNodes(List<string> pages)
{
    var nodes = new List<Dictionary<string, object>>();
    foreach (var p in pages)
    {
        var content = ReadFile(p);
        var nodeType = ExtractFrontmatterType(content);
        var titleMatch = Regex.Match(content, @"^title:\s*""?([^""\n]+)""?", RegexOptions.Multiline);
        var label = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(p);

        nodes.Add(new Dictionary<string, object>
        {
            { "id", PageId(p) },
            { "label", label },
            { "type", nodeType },
            { "color", typeColors.ContainsKey(nodeType) ? typeColors[nodeType] : typeColors["unknown"] },
            { "path", Path.GetRelativePath(repoRoot, p).Replace("\\", "/") }
        });
    }
    return nodes;
}

List<Dictionary<string, object>> BuildExtractedEdges(List<string> pages)
{
    var stemMap = pages.ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToLower(), p => PageId(p));
    var edges = new List<Dictionary<string, object>>();
    var seen = new HashSet<string>();

    foreach (var p in pages)
    {
        var content = ReadFile(p);
        var src = PageId(p);
        foreach (var link in ExtractWikilinks(content))
        {
            if (stemMap.TryGetValue(link.ToLower(), out var target) && target != src)
            {
                var key = $"{src}|{target}";
                if (!seen.Contains(key))
                {
                    seen.Add(key);
                    edges.Add(new Dictionary<string, object>
                    {
                        { "from", src },
                        { "to", target },
                        { "type", "EXTRACTED" },
                        { "color", edgeColors["EXTRACTED"] },
                        { "confidence", 1.0 }
                    });
                }
            }
        }
    }
    return edges;
}

async Task<List<Dictionary<string, object>>> BuildInferredEdgesAsync(List<string> pages, List<Dictionary<string, object>> existingEdges, Dictionary<string, object> cacheObj, Dictionary<string, JsonElement> oldCache)
{
    var newEdges = new List<Dictionary<string, object>>();
    var changedPages = new List<string>();

    foreach (var p in pages)
    {
        var content = ReadFile(p);
        var h = Sha256(content);

        bool isChanged = true;
        if (oldCache.TryGetValue(p, out var entry) && entry.ValueKind == JsonValueKind.Object)
        {
            if (entry.TryGetProperty("hash", out var hashProp) && hashProp.GetString() == h)
            {
                isChanged = false;
                var src = PageId(p);
                if (entry.TryGetProperty("edges", out var edgesArray) && edgesArray.ValueKind == JsonValueKind.Array)
                {
                    var validRels = new List<object>();
                    foreach (var rel in edgesArray.EnumerateArray())
                    {
                        if (rel.TryGetProperty("to", out var toProp))
                        {
                            var type = rel.TryGetProperty("type", out var typeProp) ? typeProp.GetString()! : "INFERRED";
                            var title = rel.TryGetProperty("relationship", out var relProp) ? relProp.GetString()! : "";
                            var confidence = rel.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 0.7;

                            newEdges.Add(new Dictionary<string, object>
                            {
                                { "from", src },
                                { "to", toProp.GetString()! },
                                { "type", type },
                                { "title", title },
                                { "label", "" },
                                { "color", edgeColors.ContainsKey(type) ? edgeColors[type] : edgeColors["INFERRED"] },
                                { "confidence", confidence }
                            });

                            // Rebuild valid rels for cache
                            validRels.Add(new {
                                to = toProp.GetString(),
                                type = type,
                                relationship = title,
                                confidence = confidence
                            });
                        }
                    }
                    cacheObj[p] = new { hash = h, edges = validRels };
                }
                else
                {
                    cacheObj[p] = new { hash = h, edges = new List<object>() };
                }
            }
        }

        if (isChanged)
        {
            changedPages.Add(p);
            cacheObj[p] = new { hash = h, edges = new List<object>() }; // default empty
        }
    }

    if (changedPages.Count == 0)
    {
        Console.WriteLine("  no changed pages — skipping semantic inference");
        return newEdges;
    }

    Console.WriteLine($"  inferring relationships for {changedPages.Count} changed pages...");

    var nodeListStr = string.Join("\n", pages.Select(p => $"- {PageId(p)} ({ExtractFrontmatterType(ReadFile(p))})"));
    var existingEdgeSummary = string.Join("\n", existingEdges.Take(30).Select(e => $"- {e["from"]} -> {e["to"]} (EXTRACTED)"));

    foreach (var p in changedPages)
    {
        var content = ReadFile(p);
        if (content.Length > 2000) content = content.Substring(0, 2000);
        var src = PageId(p);

        var prompt = $@"Analyze this wiki page and identify implicit semantic relationships to other pages in the wiki.

Source page: {src}
Content:
{content}

All available pages:
{nodeListStr}

Already-extracted edges from this page:
{existingEdgeSummary}

Return ONLY a JSON array of NEW relationships not already captured by explicit wikilinks:
[
  {{""to"": ""page-id"", ""relationship"": ""one-line description"", ""confidence"": 0.0-1.0, ""type"": ""INFERRED or AMBIGUOUS""}}
]

Rules:
- Only include pages from the available list above
- Confidence >= 0.7 -> INFERRED, < 0.7 -> AMBIGUOUS
- Do not repeat edges already in the extracted list
- Return empty array [] if no new relationships found
";
        var raw = await CallLlmAsync(prompt, 1024);
        raw = raw.Trim();
        raw = Regex.Replace(raw, @"^```(?:json)?\s*", "");
        raw = Regex.Replace(raw, @"\s*```$", "");

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var validRels = new List<object>();
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("to", out var toProp))
                    {
                        var type = rel.TryGetProperty("type", out var typeProp) ? typeProp.GetString()! : "INFERRED";
                        var title = rel.TryGetProperty("relationship", out var relProp) ? relProp.GetString()! : "";
                        var confidence = rel.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 0.7;

                        newEdges.Add(new Dictionary<string, object>
                        {
                            { "from", src },
                            { "to", toProp.GetString()! },
                            { "type", type },
                            { "title", title },
                            { "label", "" },
                            { "color", edgeColors.ContainsKey(type) ? edgeColors[type] : edgeColors["INFERRED"] },
                            { "confidence", confidence }
                        });

                        validRels.Add(new {
                            to = toProp.GetString(),
                            type = type,
                            relationship = title,
                            confidence = confidence
                        });
                    }
                }
                cacheObj[p] = new { hash = Sha256(content), edges = validRels };
            }
        }
        catch
        {
            // Ignore parse errors
        }
    }

    return newEdges;
}

// Basic Label Propagation Algorithm for community detection
Dictionary<string, int> DetectCommunities(List<Dictionary<string, object>> nodes, List<Dictionary<string, object>> edges)
{
    var graph = new UndirectedGraph<string, Edge<string>>();

    foreach (var n in nodes)
    {
        graph.AddVertex(n["id"].ToString()!);
    }

    foreach (var e in edges)
    {
        var from = e["from"].ToString()!;
        var to = e["to"].ToString()!;
        if (graph.ContainsVertex(from) && graph.ContainsVertex(to))
        {
            graph.AddEdge(new Edge<string>(from, to));
        }
    }

    if (graph.EdgeCount == 0) return new Dictionary<string, int>();

    var labels = new Dictionary<string, int>();
    int idCounter = 0;
    foreach (var v in graph.Vertices)
    {
        labels[v] = idCounter++;
    }

    var rand = new Random(42);
    bool changed = true;
    int maxIterations = 100;
    int iterations = 0;

    while (changed && iterations < maxIterations)
    {
        changed = false;
        var vertices = graph.Vertices.OrderBy(x => rand.Next()).ToList();

        foreach (var v in vertices)
        {
            var neighborLabels = new Dictionary<int, int>();
            foreach (var e in graph.AdjacentEdges(v))
            {
                var neighbor = e.Source == v ? e.Target : e.Source;
                var l = labels[neighbor];
                if (!neighborLabels.ContainsKey(l)) neighborLabels[l] = 0;
                neighborLabels[l]++;
            }

            if (neighborLabels.Count > 0)
            {
                var maxCount = neighborLabels.Values.Max();
                var bestLabels = neighborLabels.Where(kvp => kvp.Value == maxCount).Select(kvp => kvp.Key).ToList();
                var bestLabel = bestLabels[rand.Next(bestLabels.Count)];

                if (labels[v] != bestLabel)
                {
                    labels[v] = bestLabel;
                    changed = true;
                }
            }
        }
        iterations++;
    }

    var distinctLabels = labels.Values.Distinct().ToList();
    var communityMap = new Dictionary<int, int>();
    for (int i = 0; i < distinctLabels.Count; i++)
    {
        communityMap[distinctLabels[i]] = i;
    }

    var finalLabels = new Dictionary<string, int>();
    foreach (var kvp in labels)
    {
        finalLabels[kvp.Key] = communityMap[kvp.Value];
    }

    return finalLabels;
}

string RenderHtml(List<Dictionary<string, object>> nodes, List<Dictionary<string, object>> edges)
{
    var nodesJson = JsonSerializer.Serialize(nodes, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    var edgesJson = JsonSerializer.Serialize(edges, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    var legendItems = new StringBuilder();
    foreach (var kvp in typeColors)
    {
        if (kvp.Key != "unknown")
        {
            legendItems.Append($"<span style=\"background:{kvp.Value};padding:3px 8px;margin:2px;border-radius:3px;font-size:12px\">{kvp.Key}</span>");
        }
    }

    return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<title>LLM Wiki — Knowledge Graph</title>
<script src=""https://unpkg.com/vis-network/standalone/umd/vis-network.min.js""></script>
<style>
  body {{ margin: 0; background: #1a1a2e; font-family: sans-serif; color: #eee; }}
  #graph {{ width: 100vw; height: 100vh; }}
  #controls {{
    position: fixed; top: 10px; left: 10px; background: rgba(0,0,0,0.7);
    padding: 12px; border-radius: 8px; z-index: 10; max-width: 260px;
  }}
  #controls h3 {{ margin: 0 0 8px; font-size: 14px; }}
  #search {{ width: 100%; padding: 4px; margin-bottom: 8px; background: #333; color: #eee; border: 1px solid #555; border-radius: 4px; }}
  #info {{
    position: fixed; bottom: 10px; left: 10px; background: rgba(0,0,0,0.8);
    padding: 12px; border-radius: 8px; z-index: 10; max-width: 320px;
    display: none;
  }}
  #stats {{ position: fixed; top: 10px; right: 10px; background: rgba(0,0,0,0.7); padding: 10px; border-radius: 8px; font-size: 12px; }}
</style>
</head>
<body>
<div id=""controls"">
  <h3>LLM Wiki Graph</h3>
  <input id=""search"" type=""text"" placeholder=""Search nodes..."" oninput=""searchNodes(this.value)"">
  <div>{legendItems}</div>
  <div style=""margin-top:8px;font-size:11px;color:#aaa"">
    <span style=""background:#555;padding:2px 6px;border-radius:3px;margin-right:4px"">──</span> Explicit link<br>
    <span style=""background:#FF5722;padding:2px 6px;border-radius:3px;margin-right:4px"">──</span> Inferred
  </div>
</div>
<div id=""graph""></div>
<div id=""info"">
  <b id=""info-title""></b><br>
  <span id=""info-type"" style=""font-size:12px;color:#aaa""></span><br>
  <span id=""info-path"" style=""font-size:11px;color:#666""></span>
</div>
<div id=""stats""></div>
<script>
const nodes = new vis.DataSet({nodesJson});
const edges = new vis.DataSet({edgesJson});

const container = document.getElementById(""graph"");
const network = new vis.Network(container, {{ nodes, edges }}, {{
  nodes: {{
    shape: ""dot"",
    size: 12,
    font: {{ color: ""#eee"", size: 13 }},
    borderWidth: 2,
  }},
  edges: {{
    width: 1.2,
    smooth: {{ type: ""continuous"" }},
    arrows: {{ to: {{ enabled: true, scaleFactor: 0.5 }} }},
  }},
  physics: {{
    stabilization: {{ iterations: 150 }},
    barnesHut: {{ gravitationalConstant: -8000, springLength: 120 }},
  }},
  interaction: {{ hover: true, tooltipDelay: 200 }},
}});

network.on(""click"", params => {{
  if (params.nodes.length > 0) {{
    const node = nodes.get(params.nodes[0]);
    document.getElementById(""info"").style.display = ""block"";
    document.getElementById(""info-title"").textContent = node.label;
    document.getElementById(""info-type"").textContent = node.type;
    document.getElementById(""info-path"").textContent = node.path;
  }} else {{
    document.getElementById(""info"").style.display = ""none"";
  }}
}});

document.getElementById(""stats"").textContent =
  `${{nodes.length}} nodes · ${{edges.length}} edges`;

function searchNodes(q) {{
  const lower = q.toLowerCase();
  nodes.forEach(n => {{
    nodes.update({{ id: n.id, opacity: (!q || n.label.toLowerCase().includes(lower)) ? 1 : 0.15 }});
  }});
}}
</script>
</body>
</html>";
}

void AppendLog(string entry)
{
    var existing = ReadFile(logFile);
    File.WriteAllText(logFile, entry.Trim() + "\n\n" + existing, Encoding.UTF8);
}

var pages = AllWikiPages();
var today = DateTime.Now.ToString("yyyy-MM-dd");

if (pages.Count == 0)
{
    Console.WriteLine("Wiki is empty. Ingest some sources first.");
    return;
}

Console.WriteLine($"Building graph from {pages.Count} wiki pages...");
Directory.CreateDirectory(graphDir);

var oldCache = LoadCache();
var newCache = new Dictionary<string, object>();

Console.WriteLine("  Pass 1: extracting wikilinks...");
var nodes = BuildNodes(pages);
var edges = BuildExtractedEdges(pages);
Console.WriteLine($"  -> {edges.Count} extracted edges");

if (!noInfer)
{
    Console.WriteLine("  Pass 2: inferring semantic relationships...");
    var inferred = await BuildInferredEdgesAsync(pages, edges, newCache, oldCache);
    edges.AddRange(inferred);
    Console.WriteLine($"  -> {inferred.Count} inferred edges");
    SaveCache(newCache);
}
else
{
    // Just save old cache without modifications if no-infer is set
    // Actually we'd need to copy it to newCache properly, but for simplicity skip cache updating
}

Console.WriteLine("  Running Label Propagation community detection...");
var communities = DetectCommunities(nodes, edges);
foreach (var node in nodes)
{
    var id = node["id"].ToString()!;
    var commId = communities.ContainsKey(id) ? communities[id] : -1;
    if (commId >= 0)
    {
        node["color"] = communityColors[commId % communityColors.Length];
    }
    node["group"] = commId;
}

var graphData = new
{
    nodes = nodes,
    edges = edges,
    built = today
};

File.WriteAllText(graphJson, JsonSerializer.Serialize(graphData, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"  saved: graph/graph.json  ({nodes.Count} nodes, {edges.Count} edges)");

var html = RenderHtml(nodes, edges);
File.WriteAllText(graphHtml, html);
Console.WriteLine($"  saved: graph/graph.html");

var extractedCount = edges.Count(e => e["type"].ToString() == "EXTRACTED");
var inferredCount = edges.Count(e => e["type"].ToString() == "INFERRED");
AppendLog($"## [{today}] graph | Knowledge graph rebuilt\n\n{nodes.Count} nodes, {edges.Count} edges ({extractedCount} extracted, {inferredCount} inferred).");

if (openBrowser)
{
    var path = Path.GetFullPath(graphHtml);
    try
    {
        Process.Start(new ProcessStartInfo($"file://{path}") { UseShellExecute = true });
    }
    catch
    {
        Console.WriteLine($"Open {path} in your browser manually.");
    }
}
