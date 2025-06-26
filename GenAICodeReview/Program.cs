using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
string openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!;
string githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")!;
string githubRef = Environment.GetEnvironmentVariable("GITHUB_REF")!;
string repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")!;
var (owner, repo) = (repository.Split('/')[0], repository.Split('/')[1]);
string prNumber = githubRef.Split('/').Last();

string diff = RunGitCommand("git fetch origin main && git diff origin/main...HEAD");

Console.WriteLine("Fetching review from OpenAI...");
var comments = await GetOpenAiReviewComments(diff);

Console.WriteLine($"Posting {comments.Count} comments to PR...");
foreach (var comment in comments)
{
    await PostGitHubComment(owner, repo, prNumber, githubToken, comment);
}

string RunGitCommand(string cmd) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
{
    FileName = "/bin/bash",
    Arguments = $"-c \"{cmd}\"",
    RedirectStandardOutput = true,
    UseShellExecute = false
})?.StandardOutput.ReadToEnd() ?? "";

async Task<List<ReviewComment>> GetOpenAiReviewComments(string diffText)
{
    var prompt = @$"
Review this GitHub PR diff and return suggestions in JSON:
[
  {{
    ""file"": ""filename.cs"",
    ""line"": 15,
    ""comment"": ""Consider renaming for clarity.""
  }}
]

Diff:
{diffText}";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openAiApiKey);

    var requestBody = new
    {
        model = "gpt-4",
        messages = new[] { new { role = "user", content = prompt } },
        temperature = 0.2
    };

    var response = await http.PostAsync("https://api.openai.com/v1/chat/completions",
        new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

    var json = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);
    var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

    return JsonSerializer.Deserialize<List<ReviewComment>>(content!) ?? new List<ReviewComment>();
}

async Task PostGitHubComment(string owner, string repo, string prNumber, string token, ReviewComment comment)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("GenAIReviewer");

    var payload = new
    {
        path = comment.File,
        line = comment.Line,
        side = "RIGHT",
        body = comment.Comment
    };

    var url = $"https://api.github.com/repos/{owner}/{repo}/pulls/{prNumber}/comments";
    var res = await http.PostAsync(url,
        new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    if (!res.IsSuccessStatusCode)
        Console.WriteLine($"Failed to post comment: {await res.Content.ReadAsStringAsync()}");
}

record ReviewComment(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("comment")] string Comment
);