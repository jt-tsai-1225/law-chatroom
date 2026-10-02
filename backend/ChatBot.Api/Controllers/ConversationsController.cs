using ChatBot.Api.Models;
using ChatBot.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Api.Controllers;

/// <summary>
/// 聊天室。一個聊天室是一串有順序的訊息，提問時帶上 conversationId
/// 就會自動把最近幾則當作上下文（見 RAGSettings.MaxHistoryMessages）。
///
/// 未設定資料庫時整組端點回 503——而不是假裝成功卻什麼都沒存。
/// </summary>
[ApiController]
[Route("api/conversations")]
public class ConversationsController : ControllerBase
{
    private readonly IConversationStore _store;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(
        IConversationStore store, ILogger<ConversationsController> logger)
    {
        _store = store;
        _logger = logger;
    }

    private ObjectResult NotConfigured() => StatusCode(503, new
    {
        error = "聊天室功能未啟用",
        detail = "後端沒有設定 ConnectionStrings:Postgres。問答仍可使用，但不會保存紀錄。"
    });

    /// <summary>所有聊天室，依最後更新時間由新到舊。</summary>
    [HttpGet]
    public async Task<ActionResult<List<Conversation>>> List(CancellationToken ct)
    {
        if (!_store.IsConfigured) return NotConfigured();

        try
        {
            return Ok(await _store.ListAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取聊天室清單失敗");
            return StatusCode(503, new { error = "資料庫讀取失敗", detail = ex.Message });
        }
    }

    /// <summary>單一聊天室的完整訊息。</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConversationDetail>> Get(Guid id, CancellationToken ct)
    {
        if (!_store.IsConfigured) return NotConfigured();

        try
        {
            var detail = await _store.GetAsync(id, ct);
            return detail is null ? NotFound(new { error = "找不到這個聊天室" }) : Ok(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取聊天室 {Id} 失敗", id);
            return StatusCode(503, new { error = "資料庫讀取失敗", detail = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<Conversation>> Create(
        [FromBody] CreateConversationRequest? request, CancellationToken ct)
    {
        if (!_store.IsConfigured) return NotConfigured();

        try
        {
            var created = await _store.CreateAsync(request?.Title, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "建立聊天室失敗");
            return StatusCode(503, new { error = "資料庫寫入失敗", detail = ex.Message });
        }
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Rename(
        Guid id, [FromBody] RenameConversationRequest request, CancellationToken ct)
    {
        if (!_store.IsConfigured) return NotConfigured();

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "標題不可為空" });
        }

        try
        {
            return await _store.RenameAsync(id, request.Title, ct)
                ? NoContent()
                : NotFound(new { error = "找不到這個聊天室" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更名聊天室 {Id} 失敗", id);
            return StatusCode(503, new { error = "資料庫寫入失敗", detail = ex.Message });
        }
    }

    /// <summary>刪除聊天室。底下的訊息由外鍵的 ON DELETE CASCADE 一併清掉。</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!_store.IsConfigured) return NotConfigured();

        try
        {
            return await _store.DeleteAsync(id, ct)
                ? NoContent()
                : NotFound(new { error = "找不到這個聊天室" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刪除聊天室 {Id} 失敗", id);
            return StatusCode(503, new { error = "資料庫寫入失敗", detail = ex.Message });
        }
    }
}

/// <summary>可用的快取模式與其目前狀態，供前端的切換選單使用。</summary>
[ApiController]
[Route("api/cachemodes")]
public class CacheModesController : ControllerBase
{
    private readonly ILlmEndpointRegistry _endpoints;

    public CacheModesController(ILlmEndpointRegistry endpoints) => _endpoints = endpoints;

    /// <summary>
    /// 三種模式的可用性。可用與否是**探測**出來的——設定裡有端點不代表
    /// 那個容器正在跑，介面要據實顯示，不能列出來讓使用者點了才失敗。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<CacheModeStatus>>> Get(CancellationToken ct)
        => Ok(await _endpoints.GetStatusAsync(ct));
}
