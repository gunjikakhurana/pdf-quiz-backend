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
    private readonly ILogger<QuizController> _logger;

    public QuizController(IConfiguration configuration, ILogger<QuizController> logger)
    {
        _configuration = configuration;
        _httpClient = new HttpClient();
        _logger = logger;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromForm] IFormFile pdf)
    {
        _logger.LogInformation("=== New request received ===");

        // Error 1 - No file uploaded
        if (pdf == null || pdf.Length == 0)
        {
            _logger.LogWarning("Request rejected: No file uploaded");
            return BadRequest(new { error = "No PDF file uploaded" });
        }

        // Error 2 - File too large (5MB limit)
        if (pdf.Length > 5 * 1024 * 1024)
        {
            _logger.LogWarning("Request rejected: File too large ({size} bytes)", pdf.Length);
            return BadRequest(new { error = "File too large. Maximum size is 5MB" });
        }

        // Error 3 - Not a PDF
        if (pdf.ContentType != "application/pdf")
        {
            _logger.LogWarning("Request rejected: Wrong file type ({type})", pdf.ContentType);
            return BadRequest(new { error = "Only PDF files are accepted" });
        }

        _logger.LogInformation("PDF received: {name}, size: {size} bytes", pdf.FileName, pdf.Length);

        // Step 1 - Extract text from PDF
        string extractedText;
        try
        {
            _logger.LogInformation("Extracting text from PDF...");
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
            _logger.LogInformation("Extracted {chars} characters from PDF", extractedText.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to read PDF: {error}", ex.Message);
            return BadRequest(new { error = "Could not read PDF file. Make sure it is not corrupted.", detail = ex.Message });
        }

        // Error 4 - No text found
        if (string.IsNullOrWhiteSpace(extractedText))
        {
            _logger.LogWarning("No text found in PDF");
            return BadRequest(new { error = "No text found in PDF. Scanned image PDFs are not supported yet." });
        }

        // Step 2 - Call Groq API
        var apiKey = _configuration["GroqApiKey"];

        // Error 5 - API key missing
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Groq API key is missing from configuration");
            return StatusCode(500, new { error = "API key not configured on server" });
        }

        var prompt = "Generate 5 multiple choice questions from the text below.\n" +
                     "Each question must have exactly 4 options labeled A, B, C, D.\n" +
                     "Mark the correct answer.\n" +
                     "Return ONLY a JSON array, no extra text:\n" +
                     "[{\"question\":\"...\",\"options\":[\"A. ...\",\"B. ...\",\"C. ...\",\"D. ...\"],\"answer\":\"A\"}]\n\n" +
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
            _logger.LogInformation("Calling Groq API...");
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            responseBody = await response.Content.ReadAsStringAsync();

            // Error 6 - Groq API returned an error
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Groq API error: {detail}", responseBody);
                return StatusCode(500, new { error = "AI service error. Please try again later.", detail = responseBody });
            }

            _logger.LogInformation("Groq API responded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError("Could not reach Groq API: {error}", ex.Message);
            return StatusCode(500, new { error = "Could not reach AI service. Check your internet connection.", detail = ex.Message });
        }

        // Step 3 - Parse and return quiz
        try
        {
            _logger.LogInformation("Parsing Groq response...");
            using var doc = JsonDocument.Parse(responseBody);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            _logger.LogInformation("=== Request completed successfully ===");
            return Ok(content);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to parse Groq response: {error}", ex.Message);
            return StatusCode(500, new { error = "Could not parse AI response. Please try again.", detail = ex.Message });
        }
    }
}