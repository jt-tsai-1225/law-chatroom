using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ChatBot.Api.Services;

/// <summary>
/// Embedding 服務 - 用於將文本轉換為向量
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
    Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts);
}

public class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly RAGSettings _settings;

    public EmbeddingService(IHttpClientFactory httpClientFactory, IOptions<RAGSettings> settings)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            input = text,
            model = _settings.EmbeddingModel
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        _httpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.EmbeddingApiKey);

        var response = await _httpClient.PostAsync(_settings.EmbeddingEndpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        
        // 使用 JsonElement 解析
        var jsonObject = JsonDocument.Parse(json).RootElement;
        var embeddingArray = jsonObject.GetProperty("data")[0].GetProperty("embedding");
        
        var embedding = new float[embeddingArray.GetArrayLength()];
        for (int i = 0; i < embeddingArray.GetArrayLength(); i++)
        {
            embedding[i] = (float)embeddingArray[i].GetDouble();
        }
        
        return embedding;
    }

    public async Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts)
    {
        var textList = texts.ToList();
        var results = new float[textList.Count][];

        for (int i = 0; i < textList.Count; i++)
        {
            results[i] = await GenerateEmbeddingAsync(textList[i]);
        }

        return results;
    }
}
