using Microsoft.AspNetCore.Mvc;
using UglyToad.PdfPig;
using System.Text;
using System.Text.Json;

namespace PdfQuizApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QuizController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public QuizController(IConfiguration configuration)
    {
        _configuration = configuration;
        _httpClient = new HttpClient();
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromForm] IFormFile pdf)
    {
        if (pdf == null || pdf.Length == 0)
            return BadRequest("No PDF file uploaded");

        // Step 1 - Extract text from PDF
        var text = new StringBuilder();
        using (var stream = pdf.OpenReadStream())
        {
            using var document = PdfDocument.Open(stream);
            foreach (var page in document.GetPages())
            {
                text.Append(page.Text);
            }
        }

        var extractedText = text.ToString();
        if (string.IsNullOrEmpty(extractedText))
            return BadRequest("Could not extract text from PDF");

        // Step 2 - Call Groq API
        var apiKey = _configuration["GroqApiKey"];
        var prompt = $@"Generate 5 multiple choice questions from the text below.
Each question must have exactly 4 options labeled A, B, C, D.
Mark the correct answer.
Return ONLY a JSON array, no extra text:
[{{""question"":""..."",""options"":[""A. ..."",""B. ..."",""C. ..."",""D. ...""],""answer"":""A""}}]

Text: {extractedText.Substring(0, Math.Min(3000, extractedText.Length))}";

        var requestBody = new
        {
            model = "llama-3.3-70b-versatile",
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        // Step 3 - Parse and return quiz
        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return Ok(content);
    }
}