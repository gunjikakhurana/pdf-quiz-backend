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
    public async Task<IActionResult> Generate([FromForm] IFormFile pdf, [FromForm] string difficulty = "medium")
    {
        // Error 1 - No file uploaded
        if (pdf == null || pdf.Length == 0)
            return BadRequest(new { error = "No PDF file uploaded" });

        // Error 2 - File too large (5MB limit)
        if (pdf.Length > 5 * 1024 * 1024)
            return BadRequest(new { error = "File too large. Maximum size is 5MB" });

        // Error 3 - Not a PDF
        if (pdf.ContentType != "application/pdf")
            return BadRequest(new { error = "Only PDF files are accepted" });

        // Step 1 - Extract text from PDF
        string extractedText;
        try
        {
            var text = new StringBuilder();
            using (var stream = pdf.OpenReadStream())
            {
                using var document = PdfDocument.Open(stream);
                foreach (var page in document.GetPages())
                {
                    text.Append(page.Text);
                }
            }
            extractedText = text.ToString();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "Could not read PDF file. Make sure it is not corrupted.", detail = ex.Message });
        }

        // Error 4 - No text found
        if (string.IsNullOrWhiteSpace(extractedText))
            return BadRequest(new { error = "No text found in PDF. Scanned image PDFs are not supported yet." });

        // Step 2 - Call Groq API
        var apiKey = _configuration["GroqApiKey"];

        // Error 5 - API key missing
        if (string.IsNullOrEmpty(apiKey))
            return StatusCode(500, new { error = "API key not configured on server" });

        // Difficulty instruction
        var difficultyInstruction = difficulty switch
        {
            "easy" => "Generate simple recall-based questions a beginner could answer.",
            "hard" => "Generate deep analytical questions requiring expert understanding.",
            _ => "Generate moderately challenging conceptual questions."
        };

    var prompt = "Generate 5 multiple choice questions from the text below.\n" +
             "Each question must have exactly 4 options labeled A, B, C, D.\n" +
             "Mark the correct answer.\n" +
             "Also add a one-line explanation of why the correct answer is right.\n" +
             "Return ONLY a JSON array, no extra text:\n" +
             "[{\"question\":\"...\",\"options\":[\"A. ...\",\"B. ...\",\"C. ...\",\"D. ...\"],\"answer\":\"A\",\"explanation\":\"...\"}]\n\n" +
             "Text: " + extractedText.Substring(0, Math.Min(3000, extractedText.Length));
        var requestBody = new
        {
            model = "llama-3.3-70b-versatile",
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        string responseBody;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            responseBody = await response.Content.ReadAsStringAsync();

            // Error 6 - Groq API returned an error
            if (!response.IsSuccessStatusCode)
                return StatusCode(500, new { error = "AI service error. Please try again later.", detail = responseBody });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Could not reach AI service. Check your internet connection.", detail = ex.Message });
        }

        // Step 3 - Parse and return quiz
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return Ok(content);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Could not parse AI response. Please try again.", detail = ex.Message });
        }
    }
}