<script setup>
import { ref, nextTick, onMounted, computed, onUnmounted, reactive } from 'vue'
import axios from 'axios'

const API = import.meta.env.VITE_API_URL || '/api'

// ── 分頁 ────────────────────────────────────────────────────────
const tab = ref('chat')

/**
 * 後端在 Development 模式會回傳詳細錯誤，優先顯示它。
 * 只說「發生錯誤」等於把訊息丟掉，排查時得回去翻容器 log。
 */
function describeError(err) {
  const d = err?.response?.data
  if (typeof d === 'string' && d.trim()) return d
  if (d?.error) return d.hint ? `${d.error}（${d.hint}）` : d.error
  if (d?.title) return d.title
  if (err?.message) return err.message
  return '無法連線到後端'
}

// ══════════════════════════════════════════════════════════════
//  快取模式
// ══════════════════════════════════════════════════════════════

/**
 * 三種模式各是一個獨立的 vLLM 實例——LMCache 的設定在引擎啟動時就固定了，
 * 無法用單次請求的參數切換。
 *
 * 可用性是後端**探測**出來的，不是設定出來的：一張 48 GB 的卡放不下三個
 * 實例（光權重就 43.5 GB），所以「無快取」平常是停的。這裡據實顯示，
 * 不可用的選項直接停用——列出來讓人點了才失敗是更差的做法。
 */
const cacheModes = ref([])
const cacheMode = ref('cacheblend')

async function loadCacheModes() {
  try {
    const { data } = await axios.get(`${API}/cachemodes`)
    cacheModes.value = data || []

    // 目前選的模式若不可用，換到第一個可用的，免得送出才失敗
    const current = cacheModes.value.find(m => m.mode === cacheMode.value)
    if (!current?.reachable) {
      const fallback = cacheModes.value.find(m => m.reachable)
      if (fallback) cacheMode.value = fallback.mode
    }
  } catch {
    // 端點不存在時退回單一模式，不讓整個介面壞掉
    cacheModes.value = [
      { mode: 'cacheblend', displayName: 'CacheBlend（非前綴複用）',
        configured: true, reachable: true }
    ]
  }
}

function modeLabel(mode) {
  return cacheModes.value.find(m => m.mode === mode)?.displayName || mode
}

// ══════════════════════════════════════════════════════════════
//  聊天室
// ══════════════════════════════════════════════════════════════

const conversations = ref([])
const activeId = ref(null)
/** 後端沒設定資料庫時為 false：問答照常，只是不保存。 */
const roomsEnabled = ref(true)
const roomsNote = ref('')
const renamingId = ref(null)
const renameDraft = ref('')

async function loadConversations() {
  try {
    const { data } = await axios.get(`${API}/conversations`)
    conversations.value = data || []
    roomsEnabled.value = true
    roomsNote.value = ''
  } catch (err) {
    if (err?.response?.status === 503) {
      roomsEnabled.value = false
      roomsNote.value = err.response.data?.detail || '聊天室功能未啟用'
    } else {
      roomsNote.value = describeError(err)
    }
    conversations.value = []
  }
}

async function newConversation() {
  if (!roomsEnabled.value) return
  try {
    const { data } = await axios.post(`${API}/conversations`, {})
    conversations.value.unshift(data)
    await openConversation(data.id)
  } catch (err) {
    roomsNote.value = describeError(err)
  }
}

async function openConversation(id) {
  if (!roomsEnabled.value) return
  activeId.value = id
  messages.value = []

  try {
    const { data } = await axios.get(`${API}/conversations/${id}`)
    messages.value = (data.messages || []).map(fromStored)
    await scrollDown()
  } catch (err) {
    messages.value = [{ role: 'error', content: describeError(err), at: new Date() }]
  }
}

async function deleteConversation(id) {
  if (!confirm('刪除這個聊天室？底下的訊息會一併清除，無法復原。')) return
  try {
    await axios.delete(`${API}/conversations/${id}`)
    conversations.value = conversations.value.filter(c => c.id !== id)
    if (activeId.value === id) { activeId.value = null; messages.value = [] }
  } catch (err) {
    roomsNote.value = describeError(err)
  }
}

function startRename(c) {
  renamingId.value = c.id
  renameDraft.value = c.title
  nextTick(() => document.getElementById(`rename-${c.id}`)?.focus())
}

async function commitRename(c) {
  const title = renameDraft.value.trim()
  renamingId.value = null
  if (!title || title === c.title) return

  try {
    await axios.patch(`${API}/conversations/${c.id}`, { title })
    c.title = title
  } catch (err) {
    roomsNote.value = describeError(err)
  }
}

/** 資料庫裡的一則訊息 → 畫面上的一則訊息。 */
function fromStored(m) {
  if (m.role !== 'assistant') {
    return { role: 'user', content: m.content, at: m.createdAt }
  }

  return {
    role: 'bot',
    content: m.content,
    at: m.createdAt,
    stats: {
      ttft: m.ttftMs,
      total: m.totalMs,
      promptTokens: m.promptTokens,
      cachedTokens: m.cachedTokens,
      hitRate: m.cacheHitRate,
      articles: m.retrievedArticles || [],
      order: m.chunkOrder,
      permutation: [],
      prefixCeiling: m.prefixCeilingTokens,
      overPrefix: m.cachedOverPrefixCeiling,
      cacheMode: m.cacheMode
    }
  }
}

// ══════════════════════════════════════════════════════════════
//  問答
// ══════════════════════════════════════════════════════════════

const messages = ref([])
const draft = ref('')
const sending = ref(false)
const scroller = ref(null)

/**
 * 片段排列方式。正常使用固定為 default；其餘選項是驗證 CacheBlend
 * 非前綴複用用的，放在介面上是為了能當場示範，不是給一般使用者調的。
 */
const chunkOrder = ref('default')

const ORDER_MODES = [
  { value: 'default', label: '依快取重排（正式行為）' },
  { value: 'relevance', label: '相關度原序' },
  { value: 'swap-tail', label: '對調末兩段' },
  { value: 'swap-head', label: '對調前兩段' },
  { value: 'reverse', label: '完全倒轉' },
  { value: 'shuffle', label: '完全打亂' }
]

/**
 * 解析一個 SSE 事件區塊（以空行分隔，「event: 名稱」與「data: JSON」各一行）。
 * 後端 /api/chat/stream 的三種事件：delta（生成中的文字）、done（完整量測）、
 * error（生成中斷；SSE 開始後無法改 HTTP 狀態碼，錯誤改走事件）。
 */
function parseSseBlock(block) {
  let event = 'message'
  const dataLines = []
  for (const line of block.split('\n')) {
    if (line.startsWith('event:')) {
      event = line.slice(6).trim()
    } else if (line.startsWith('data:')) {
      dataLines.push(line.slice(5).trimStart())
    }
  }
  if (dataLines.length === 0) return null
  try {
    return { event, data: JSON.parse(dataLines.join('\n')) }
  } catch {
    return null
  }
}

/**
 * 送出問題並以串流讀回回答。onDelta 在每段文字到達時被呼叫。
 * 回傳 done 事件的資料（與 /api/chat 同一包 ChatResponse）。
 *
 * 失敗以 throw 表達：HTTP 狀態碼問題在讀流之前就能發現，掛 err.status
 * 供呼叫端判斷（503 = 選了未啟動的模式）；串流中的錯誤走 error 事件。
 */
async function streamChat(payload, onDelta) {
  const res = await fetch(`${API}/chat/stream`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  if (!res.ok) {
    let message = `HTTP ${res.status}`
    try {
      const data = await res.json()
      if (data?.error) message = data.hint ? `${data.error}（${data.hint}）` : data.error
    } catch { /* 錯誤內容不一定是 JSON */ }
    const err = new Error(message)
    err.status = res.status
    throw err
  }

  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let done = null

  try {
    for (;;) {
      const { done: finished, value } = await reader.read()
      if (finished) break
      buffer += decoder.decode(value, { stream: true })

      // 事件以空行分隔；殘缺的最後一段留給下一次 read 補齊
      let sep
      while ((sep = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, sep)
        buffer = buffer.slice(sep + 2)

        const ev = parseSseBlock(block)
        if (!ev) continue

        if (ev.event === 'delta') {
          onDelta(ev.data?.text || '')
        } else if (ev.event === 'done') {
          done = ev.data
        } else if (ev.event === 'error') {
          throw new Error(ev.data?.error || '生成中斷')
        }
      }
    }
  } finally {
    // 中途拋出（error 事件、元件卸載）時把連線收掉，不留半開的 socket
    if (done === null) reader.cancel().catch(() => {})
  }

  return done
}

async function send() {
  const text = draft.value.trim()
  if (!text || sending.value) return

  // 沒有聊天室就先開一個，不要讓使用者多按一次
  if (roomsEnabled.value && !activeId.value) {
    await newConversation()
  }

  const firstTurn = messages.value.length === 0

  messages.value.push({ role: 'user', content: text, at: new Date() })
  draft.value = ''
  sending.value = true
  await scrollDown()

  // 串流：先放一個空氣泡，文字一到就長出來。
  // 量測資料（TTFT、命中率…）在 done 事件才補上，之前統計區不顯示。
  // 內容用模型原始輸出——資料庫存的也是這一份，用 reply 的話同一則訊息
  // 在「剛送出」與「重新載入後」會長得不一樣。
  //
  // ⚠ 必須用 reactive() 包起來：push 進 ref 陣列之後，模板綁定的是
  // Vue 的代理物件，而這個變數仍指向原始物件——直接對它 += 的話
  // 改動不會被追蹤，文字會在 sending 轉 false 時才一次全部出現
  // （正是「串流失效」的那種症狀）。reactive 代理 push 進陣列後
  // Vue 會沿用同一個代理，之後的修改才會即時重繪。
  const bot = reactive({ role: 'bot', content: '', at: new Date(), streaming: true, stats: null })
  messages.value.push(bot)

  try {
    const done = await streamChat(
      {
        message: text,
        chunkOrder: chunkOrder.value,
        cacheMode: cacheMode.value,
        conversationId: activeId.value
      },
      t => {
        bot.content += t
        scrollDown()
      }
    )

    if (!done) throw new Error('連線中斷，答案可能不完整')

    // 查無條文時沒有任何 delta，內容在 done 事件才出現
    if (!bot.content) bot.content = done.rawReply || done.reply || ''
    bot.persisted = done.persisted
    bot.stats = {
      ttft: done.ttftMilliseconds,
      total: done.totalMilliseconds,
      promptTokens: done.promptTokens,
      cachedTokens: done.cachedTokens,
      hitRate: done.cacheHitRate,
      articles: done.retrievedArticles || [],
      order: done.chunkOrderApplied,
      permutation: done.chunkPermutation || [],
      reordered: done.reorderedForCache,
      prefixCeiling: done.prefixOnlyCeilingTokens,
      overPrefix: done.cachedOverPrefixCeiling,
      tokensPerSecond: done.tokensPerSecond,
      cacheMode: done.cacheMode,
      historyUsed: done.historyMessagesUsed
    }

    if (firstTurn) await autoTitle(text)
    await refreshActiveSummary()
  } catch (err) {
    // 已經吐出來的字保留，後面補一則錯誤訊息；一個字都沒有就把空泡泡收掉
    if (!bot.content) {
      const i = messages.value.indexOf(bot)
      if (i >= 0) messages.value.splice(i, 1)
    }
    messages.value.push({
      role: 'error',
      content: err?.message || describeError(err),
      at: new Date()
    })

    // 503 多半是選了一個沒啟動的模式——順手更新可用性，
    // 讓選單立刻反映現況，而不是等使用者再失敗一次
    if (err?.status === 503) await loadCacheModes()
  } finally {
    bot.streaming = false
    sending.value = false
    await scrollDown()
  }
}

/** 第一則訊息後把「新對話」換成問題的前 30 字，側欄才認得出是哪一個。 */
async function autoTitle(firstMessage) {
  const c = conversations.value.find(x => x.id === activeId.value)
  if (!c || c.title !== '新對話') return

  const title = firstMessage.length > 30
    ? firstMessage.slice(0, 30) + '…'
    : firstMessage

  try {
    await axios.patch(`${API}/conversations/${c.id}`, { title })
    c.title = title
  } catch { /* 命名失敗不影響對話，沉默即可 */ }
}

function refreshActiveSummary() {
  const c = conversations.value.find(x => x.id === activeId.value)
  if (!c) return
  c.messageCount = messages.value.filter(m => m.role !== 'error').length
  c.updatedAt = new Date().toISOString()
}

async function scrollDown() {
  await nextTick()
  if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
}

// ══════════════════════════════════════════════════════════════
//  知識庫
// ══════════════════════════════════════════════════════════════

const laws = ref([])
const totalChunks = ref(0)
const warmedChunks = ref(0)
const kbLoading = ref(false)
const kbError = ref('')

const uploadFile = ref(null)
const uploadTitle = ref('')
const uploadWarmup = ref(true)
const job = ref(null)
let pollTimer = null

const warming = ref(false)
const warmupResult = ref(null)

const STAGE_TEXT = {
  queued: '排隊中',
  storing: '保存原始檔',
  parsing: '解析與切分',
  embedding: '向量化並寫入 Qdrant',
  warmup: '預算 KV',
  done: '完成',
  failed: '失敗'
}

const ALL_STAGES = ['storing', 'parsing', 'embedding', 'warmup', 'done']

/**
 * 這次實際會經過的階段。
 *
 * 沒有勾選預熱時必須把 warmup 這一格拿掉——先前是固定畫出五格，
 * 而工作完成後所有排在 done 之前的格子都被標成「完成」，
 * 於是即使預熱從未執行，畫面上也顯示它做完了。那是在騙人。
 */
const stages = computed(() =>
  ALL_STAGES.filter(s => s !== 'warmup' || job.value?.warmupRequested)
)

const stageIndex = computed(() => {
  if (!job.value) return -1
  return stages.value.indexOf(job.value.stage)
})

async function loadKnowledgeBase() {
  kbLoading.value = true
  kbError.value = ''
  try {
    const { data } = await axios.get(`${API}/knowledgebase/documents`)
    laws.value = data.laws || []
    totalChunks.value = data.totalChunks || 0
    warmedChunks.value = data.warmedChunks || 0
  } catch (err) {
    kbError.value = describeError(err)
  } finally {
    kbLoading.value = false
  }
}

function pickFile(event) {
  uploadFile.value = event.target.files?.[0] || null
}

async function upload() {
  if (!uploadFile.value) return

  const form = new FormData()
  form.append('file', uploadFile.value)

  const params = new URLSearchParams()
  if (uploadTitle.value.trim()) params.set('title', uploadTitle.value.trim())
  params.set('warmup', String(uploadWarmup.value))

  try {
    const { data } = await axios.post(
      `${API}/knowledgebase/upload?${params}`, form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
    job.value = { jobId: data.jobId, stage: 'queued', fileName: data.fileName }
    startPolling(data.jobId)
  } catch (err) {
    job.value = { stage: 'failed', error: describeError(err) }
  }
}

/**
 * 輪詢匯入進度。
 *
 * 上傳端點只回 jobId 就結束——一部民法要跑三百多次 embedding 再加上
 * KV 預熱，耗時以分鐘計，撐不過一個 HTTP 請求。
 */
function startPolling(id) {
  stopPolling()
  pollTimer = setInterval(async () => {
    try {
      const { data } = await axios.get(`${API}/knowledgebase/jobs/${id}`)
      job.value = data
      if (data.stage === 'done' || data.stage === 'failed') {
        stopPolling()
        if (data.stage === 'done') await loadKnowledgeBase()
      }
    } catch (err) {
      job.value = { ...job.value, stage: 'failed', error: describeError(err) }
      stopPolling()
    }
  }, 1500)
}

function stopPolling() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null }
}

async function runWarmup(law) {
  warming.value = true
  warmupResult.value = null
  try {
    const { data } = await axios.post(`${API}/knowledgebase/warmup`, { law: law || null })
    warmupResult.value = data
  } catch (err) {
    warmupResult.value = { error: describeError(err) }
  } finally {
    warming.value = false
    // 預熱改變了狀態欄該顯示的內容，重新取一次
    await loadKnowledgeBase()
  }
}

function fmtTime(d) {
  return new Date(d).toLocaleTimeString('zh-TW', { hour: '2-digit', minute: '2-digit' })
}

function fmtDate(d) {
  return new Date(d).toLocaleDateString('zh-TW', { month: 'numeric', day: 'numeric' })
}

function fmtNum(n) {
  return typeof n === 'number' ? n.toLocaleString('zh-TW') : '—'
}

onMounted(async () => {
  await Promise.all([loadKnowledgeBase(), loadCacheModes(), loadConversations()])
})
onUnmounted(stopPolling)
</script>

<template>
  <div class="app">
    <header class="top">
      <div class="brand">
        <h1>法律條文問答</h1>
        <span class="sub">民法 · 公司法 · CacheBlend 驗證環境</span>
      </div>
      <nav class="tabs">
        <button :class="{ on: tab === 'chat' }" @click="tab = 'chat'">問答</button>
        <button :class="{ on: tab === 'kb' }" @click="tab = 'kb'">知識庫</button>
      </nav>
    </header>

    <!-- ══════════════ 問答 ══════════════ -->
    <section v-show="tab === 'chat'" class="chat-layout">

      <!-- ── 側欄：聊天室 ── -->
      <aside class="rooms">
        <button class="new-room" @click="newConversation" :disabled="!roomsEnabled">
          ＋ 新對話
        </button>

        <p v-if="!roomsEnabled" class="rooms-note">
          {{ roomsNote }}<br />
          <span class="dim">問答仍可使用，只是不會留下紀錄。</span>
        </p>
        <p v-else-if="roomsNote" class="err small">{{ roomsNote }}</p>

        <ul v-if="roomsEnabled" class="room-list">
          <li
            v-for="c in conversations"
            :key="c.id"
            :class="['room', { on: c.id === activeId }]"
            @click="openConversation(c.id)"
          >
            <input
              v-if="renamingId === c.id"
              :id="`rename-${c.id}`"
              v-model="renameDraft"
              class="rename"
              @keyup.enter="commitRename(c)"
              @blur="commitRename(c)"
              @click.stop
            />
            <template v-else>
              <span class="room-title">{{ c.title }}</span>
              <span class="room-meta">
                {{ fmtDate(c.updatedAt) }} · {{ c.messageCount }} 則
              </span>
              <span class="room-acts">
                <button title="重新命名" @click.stop="startRename(c)">✎</button>
                <button title="刪除" @click.stop="deleteConversation(c.id)">✕</button>
              </span>
            </template>
          </li>
        </ul>

        <p v-if="roomsEnabled && conversations.length === 0" class="rooms-note">
          還沒有任何對話。直接在右邊輸入問題就會自動建立一個。
        </p>
      </aside>

      <!-- ── 主區：對話 ── -->
      <div class="panel">
        <div class="thread" ref="scroller">
          <p v-if="messages.length === 0" class="empty">
            問一個民法或公司法的問題，例如：<br />
            「公司重整時，董事拒絕移交帳冊文件，會被處以多少罰金？」
          </p>

          <div v-for="(m, i) in messages" :key="i" :class="['turn', m.role, { streaming: m.streaming }]">
            <div class="bubble">
              <p class="text">{{ m.content }}</p>

              <p v-if="m.role === 'bot' && m.persisted === false" class="warn small">
                ⚠ 這則回覆沒有存進資料庫，重新整理後會消失
              </p>

              <div v-if="m.stats" class="stats">
                <div class="row">
                  <span v-if="m.stats.cacheMode" class="badge mode">
                    {{ modeLabel(m.stats.cacheMode) }}
                  </span>
                  <span class="k">TTFT</span>
                  <span class="v strong">{{ fmtNum(m.stats.ttft) }} ms</span>
                  <span class="k">總耗時</span>
                  <span class="v">{{ fmtNum(m.stats.total) }} ms</span>
                  <span v-if="m.stats.tokensPerSecond" class="k">生成速率</span>
                  <span v-if="m.stats.tokensPerSecond" class="v">
                    {{ m.stats.tokensPerSecond.toFixed(1) }} tok/s
                  </span>
                </div>

                <div class="row">
                  <span class="k">KV 命中</span>
                  <span class="v strong">
                    {{ fmtNum(m.stats.cachedTokens) }} / {{ fmtNum(m.stats.promptTokens) }}
                    （{{ m.stats.hitRate?.toFixed(2) }}%）
                  </span>
                  <template v-if="m.stats.historyUsed">
                    <span class="k">帶入歷史</span>
                    <span class="v">{{ m.stats.historyUsed }} 則</span>
                  </template>
                </div>

                <!--
                  前綴上限是判讀命中率的必要對照：命中率再高，若沒有超過
                  「純前綴快取所能提供的上限」，就無法證明非前綴複用有在運作。
                -->
                <div class="row">
                  <span class="k">前綴快取上限</span>
                  <span class="v">{{ fmtNum(m.stats.prefixCeiling) }} tokens</span>
                  <span class="k">實際 ÷ 上限</span>
                  <span :class="['v', 'badge', m.stats.overPrefix > 1.05 ? 'good' : 'plain']">
                    {{ m.stats.overPrefix }}×
                  </span>
                </div>
                <p v-if="m.stats.overPrefix > 1.05" class="note">
                  超出前綴快取所能解釋的範圍 → 非前綴複用（CacheBlend）確實生效
                </p>

                <div class="row wrap">
                  <span class="k">排列</span>
                  <span class="v">
                    {{ m.stats.order }}
                    <template v-if="m.stats.permutation?.length">
                      [{{ m.stats.permutation.join(',') }}]
                    </template>
                    <template v-if="m.stats.reordered">· 已依快取重排</template>
                  </span>
                </div>

                <div class="row wrap">
                  <span class="k">引用片段</span>
                  <span class="v articles">
                    <code v-for="(a, j) in m.stats.articles" :key="j">{{ a }}</code>
                  </span>
                </div>
              </div>
            </div>
            <span class="time">{{ fmtTime(m.at) }}</span>
          </div>
        </div>

        <!--
          免責聲明固定顯示一次，不再附在每一則回覆後面。
          每則都重複一遍只會讓人略過不看，而且會被存進對話歷史送回模型。
        -->
        <p class="disclaimer">
          ⚠️ 以上資訊僅供參考，不構成正式法律意見。具體法律問題請諮詢專業律師。
        </p>

        <div class="composer">
          <select v-model="cacheMode" title="要打哪一個推論端點">
            <option
              v-for="m in cacheModes"
              :key="m.mode"
              :value="m.mode"
              :disabled="!m.reachable"
            >
              {{ m.displayName }}{{ m.reachable ? '' : '（未啟動）' }}
            </option>
          </select>

          <select v-model="chunkOrder" title="片段排列方式（驗證用）">
            <option v-for="o in ORDER_MODES" :key="o.value" :value="o.value">{{ o.label }}</option>
          </select>

          <input
            v-model="draft"
            @keyup.enter="send"
            :disabled="sending"
            placeholder="輸入法律問題…"
          />
          <button @click="send" :disabled="!draft.trim() || sending">
            {{ sending ? '生成中…' : '送出' }}
          </button>
        </div>
      </div>
    </section>

    <!-- ══════════════ 知識庫 ══════════════ -->
    <section v-show="tab === 'kb'" class="panel scroll">
      <div class="card">
        <h2>上傳法規 PDF</h2>
        <p class="hint">
          流程：保存原始檔 → 解析切分 → 向量化寫入 Qdrant → 預算 KV。
          重新上傳同一部法典會<strong>取代</strong>既有片段。
        </p>

        <div class="form">
          <input type="file" accept="application/pdf" @change="pickFile" />
          <input v-model="uploadTitle" placeholder="法典名稱（留空則讀 PDF 內的「法規名稱：」）" />
          <label class="check">
            <input type="checkbox" v-model="uploadWarmup" />
            上傳後立即預算 KV
          </label>
          <button class="primary" @click="upload" :disabled="!uploadFile">開始匯入</button>
        </div>

        <div v-if="job" class="job">
          <div class="steps">
            <span
              v-for="(s, i) in stages"
              :key="s"
              :class="['step', {
                done: stageIndex > i,
                now: job.stage === s,
                failed: job.stage === 'failed'
              }]"
            >{{ STAGE_TEXT[s] }}</span>

            <span v-if="!job.warmupRequested" class="step skipped">
              預算 KV（未勾選，略過）
            </span>
          </div>

          <p v-if="job.stage === 'failed'" class="err">匯入失敗：{{ job.error }}</p>
          <p v-else class="state">
            {{ STAGE_TEXT[job.stage] || job.stage }}
            <template v-if="job.elapsedMilliseconds">
              · 已耗時 {{ (job.elapsedMilliseconds / 1000).toFixed(1) }} 秒
            </template>
          </p>

          <dl v-if="job.chunkCount" class="kv">
            <div><dt>法典</dt><dd>{{ job.title }}</dd></div>
            <div><dt>條文數</dt><dd>{{ fmtNum(job.articleCount) }}</dd></div>
            <div><dt>片段數</dt><dd>{{ fmtNum(job.chunkCount) }}</dd></div>
            <div><dt>已向量化</dt><dd>{{ fmtNum(job.embeddedCount) }}</dd></div>
            <div v-if="job.replacedCount"><dt>取代舊片段</dt><dd>{{ fmtNum(job.replacedCount) }}</dd></div>
            <div><dt>原始檔</dt><dd>{{ job.storageBackend }} · {{ job.storageKey }}</dd></div>
          </dl>

          <div v-if="job.warmup" class="kv">
            <div>
              <dt>KV 預熱</dt>
              <dd>
                {{ fmtNum(job.warmup.processedChunks) }} / {{ fmtNum(job.warmup.totalChunks) }} 片段
                · {{ job.warmup.batches }} 批（失敗 {{ job.warmup.failedBatches }}）
                · {{ (job.warmup.elapsedMilliseconds / 1000).toFixed(1) }} 秒
              </dd>
            </div>
          </div>

          <p v-for="(w, i) in (job.warnings || [])" :key="i" class="warn">⚠ {{ w }}</p>
        </div>
      </div>

      <div class="card">
        <div class="card-head">
          <h2>目前內容</h2>
          <button @click="loadKnowledgeBase" :disabled="kbLoading">
            {{ kbLoading ? '讀取中…' : '重新整理' }}
          </button>
        </div>

        <p v-if="kbError" class="err">{{ kbError }}</p>
        <p v-else-if="laws.length === 0" class="hint">知識庫是空的，請先上傳法規 PDF。</p>

        <table v-else class="table">
          <thead>
            <tr>
              <th>法典</th><th>片段數</th><th>總字數</th><th>字數中位數</th>
              <th>章節數</th><th>KV 預熱</th><th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="l in laws" :key="l.law">
              <td>{{ l.law }}</td>
              <td>{{ fmtNum(l.chunks) }}</td>
              <td>{{ fmtNum(l.totalChars) }}</td>
              <td>{{ fmtNum(l.medianChars) }}</td>
              <td>{{ fmtNum(l.chapters) }}</td>
              <td>
                <span v-if="l.warmed" class="badge good">已預熱</span>
                <span v-else-if="l.warmedChunks > 0" class="badge partial">
                  部分 {{ l.warmedChunks }}/{{ l.chunks }}
                </span>
                <span v-else class="badge plain">未預熱</span>
              </td>
              <td><button @click="runWarmup(l.law)" :disabled="warming">預熱</button></td>
            </tr>
          </tbody>
          <tfoot>
            <tr>
              <td>合計</td>
              <td>{{ fmtNum(totalChunks) }}</td>
              <td colspan="3"></td>
              <td>{{ fmtNum(warmedChunks) }} / {{ fmtNum(totalChunks) }}</td>
              <td><button @click="runWarmup(null)" :disabled="warming">全部預熱</button></td>
            </tr>
          </tfoot>
        </table>

        <!--
          必須講明這是推估而非事實，否則「已預熱」這三個字會被當成保證。
          後端記的是自己送過什麼，引擎那邊被清空時它不會知道。
        -->
        <p class="hint">
          預熱狀態由後端的送出紀錄推估，並非向引擎查詢。
          vLLM 或後端任一方重啟後會失準，重新預熱即可對齊。
        </p>

        <!--
          這段說明必須留著。L2 的索引存在記憶體，服務重啟後磁碟上的 KV
          檔案還在、內容也完整，但讀不到——預熱是每次啟動都要重跑的動作，
          不是一次性的資料準備。不寫清楚很容易誤以為已經永久生效。
        -->
        <p class="hint">
          KV 預熱讓片段的 KV 先進入快取，第一位使用者就不必承受冷啟成本。
          <strong>服務重啟後 L2 索引不會重建，需要重新預熱。</strong>
        </p>

        <!--
          預熱只打 CacheBlend 端點：後端的預熱請求不帶快取模式，走預設端點。
          先前這裡寫成「對目前選定的端點生效」，是錯的——選 LMCache 後按預熱，
          寫進去的仍是 CacheBlend。
          LMCache 是前綴快取，預熱用的是「多個片段串接」的 prompt，
          與日後逐題組出的 prompt 前綴不同，預熱不會讓它命中；
          它的快取由實際提問自然累積，所以也不需要預熱。
        -->
        <p class="hint">
          <strong>預熱只對 CacheBlend 生效。</strong>
          純 LMCache 是前綴快取，靠實際提問自然累積，不需要也無法預熱；
          無快取則沒有快取可熱。
        </p>

        <div v-if="warming" class="state">預熱中，這可能需要一到兩分鐘…</div>

        <div v-if="warmupResult" class="kv">
          <p v-if="warmupResult.error" class="err">{{ warmupResult.error }}</p>
          <template v-else>
            <div>
              <dt>預熱結果</dt>
              <dd>
                {{ fmtNum(warmupResult.processedChunks) }} / {{ fmtNum(warmupResult.totalChunks) }} 片段
                · {{ warmupResult.batches }} 批（失敗 {{ warmupResult.failedBatches }}）
                · {{ (warmupResult.elapsedMilliseconds / 1000).toFixed(1) }} 秒
              </dd>
            </div>
            <div>
              <dt>prompt 總量</dt>
              <dd>
                {{ fmtNum(warmupResult.totalPromptTokens) }} tokens
                （其中命中既有快取 {{ fmtNum(warmupResult.totalCachedTokens) }}）
              </dd>
            </div>
          </template>
        </div>
      </div>
    </section>
  </div>
</template>

<style>
:root {
  --bg: #0f1117;
  --card: #171a23;
  --line: #262a36;
  --fg: #e6e8ee;
  --dim: #939aad;
  --accent: #5b8cff;
  --good: #3fbf7f;
  --warn: #e0a83c;
  --bad: #e5645e;
}

* { margin: 0; padding: 0; box-sizing: border-box; }

body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans TC', sans-serif;
  background: var(--bg);
  color: var(--fg);
  font-size: 14px;
  line-height: 1.6;
}

.app { max-width: 1240px; margin: 0 auto; height: 100vh; display: flex; flex-direction: column; }

.top {
  display: flex; align-items: center; justify-content: space-between;
  padding: 16px 20px; border-bottom: 1px solid var(--line);
}
.brand h1 { font-size: 1.15rem; font-weight: 600; }
.sub { color: var(--dim); font-size: 0.8rem; }

.tabs button {
  background: none; border: 1px solid var(--line); color: var(--dim);
  padding: 6px 16px; cursor: pointer; font-size: 0.9rem;
}
.tabs button:first-child { border-radius: 6px 0 0 6px; }
.tabs button:last-child { border-radius: 0 6px 6px 0; border-left: none; }
.tabs button.on { background: var(--accent); color: #fff; border-color: var(--accent); }

.panel { flex: 1; display: flex; flex-direction: column; min-height: 0; }
.panel.scroll { overflow-y: auto; padding: 20px; gap: 20px; }

/* ── 聊天室側欄 ───────────────────────────────────── */
.chat-layout { flex: 1; display: flex; min-height: 0; }

.rooms {
  width: 240px; flex-shrink: 0; border-right: 1px solid var(--line);
  display: flex; flex-direction: column; padding: 12px; gap: 10px; overflow-y: auto;
}

.new-room {
  background: var(--accent); border: 1px solid var(--accent); color: #fff;
  padding: 8px; border-radius: 6px; cursor: pointer; font-size: 0.88rem;
  font-family: inherit;
}
.new-room:disabled { opacity: 0.4; cursor: not-allowed; }

.rooms-note { color: var(--dim); font-size: 0.78rem; line-height: 1.5; }
.rooms-note .dim { color: #6d7389; }

.room-list { list-style: none; display: flex; flex-direction: column; gap: 2px; }

.room {
  padding: 8px 10px; border-radius: 6px; cursor: pointer;
  display: grid; grid-template-columns: 1fr auto; gap: 2px 6px; align-items: center;
}
.room:hover { background: var(--card); }
.room.on { background: #1d2740; }

.room-title {
  font-size: 0.86rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.room-meta { grid-column: 1; color: var(--dim); font-size: 0.72rem; }
.room-acts { grid-row: 1 / span 2; grid-column: 2; display: flex; gap: 2px; opacity: 0; }
.room:hover .room-acts, .room.on .room-acts { opacity: 1; }
.room-acts button {
  background: none; border: none; color: var(--dim); cursor: pointer;
  font-size: 0.8rem; padding: 2px 4px; border-radius: 4px;
}
.room-acts button:hover { color: var(--fg); background: var(--line); }

.rename {
  grid-column: 1 / -1; background: var(--bg); border: 1px solid var(--accent);
  color: var(--fg); padding: 4px 6px; border-radius: 4px; font-size: 0.84rem;
  font-family: inherit; width: 100%;
}

/* ── 問答 ─────────────────────────────────────────── */
.thread { flex: 1; overflow-y: auto; padding: 20px; }
.empty { color: var(--dim); text-align: center; margin-top: 60px; }

.turn { margin-bottom: 20px; display: flex; flex-direction: column; }
.turn.user { align-items: flex-end; }
.bubble {
  max-width: 88%; padding: 12px 14px; border-radius: 10px;
  background: var(--card); border: 1px solid var(--line);
}
.turn.user .bubble { background: #1d2740; border-color: #2c3b5e; }
.turn.error .bubble { border-color: var(--bad); }
.text { white-space: pre-wrap; word-break: break-word; }
.time { color: var(--dim); font-size: 0.72rem; margin-top: 4px; }

/* 串流生成中的游標，done 事件後隨 streaming 標記消失 */
.turn.streaming .text::after {
  content: '▍';
  margin-left: 2px;
  animation: blink 1s steps(2, start) infinite;
}
@keyframes blink { to { visibility: hidden; } }

.disclaimer {
  color: var(--dim); font-size: 0.76rem; text-align: center;
  padding: 6px 20px 0; border-top: 1px solid var(--line);
}

.stats {
  margin-top: 12px; padding-top: 10px; border-top: 1px solid var(--line);
  font-size: 0.8rem;
}
.stats .row { display: flex; gap: 8px; align-items: baseline; margin-bottom: 4px; }
.stats .row.wrap { flex-wrap: wrap; }
.stats .k { color: var(--dim); white-space: nowrap; }
.stats .v { margin-right: 10px; }
.stats .v.strong { font-weight: 600; }
.badge { padding: 1px 7px; border-radius: 10px; font-weight: 600; }
.badge.good { background: rgba(63, 191, 127, 0.18); color: var(--good); }
.badge.plain { background: var(--line); color: var(--dim); }
.badge.partial { background: rgba(224, 168, 60, 0.18); color: var(--warn); }
.badge.mode {
  background: rgba(91, 140, 255, 0.18); color: var(--accent);
  font-size: 0.74rem; margin-right: 4px;
}
.step.skipped { background: transparent; border: 1px dashed var(--line); color: var(--dim); }
.note { color: var(--good); font-size: 0.76rem; margin: 2px 0 6px; }
.small { font-size: 0.76rem; }
.articles code {
  background: var(--line); padding: 1px 6px; border-radius: 4px;
  margin-right: 5px; font-size: 0.76rem;
}

.composer {
  display: flex; gap: 8px; padding: 14px 20px; border-top: 1px solid var(--line);
}
.composer select, .composer input, .form input, .form button, .card button {
  background: var(--card); border: 1px solid var(--line); color: var(--fg);
  padding: 9px 12px; border-radius: 6px; font-size: 0.88rem; font-family: inherit;
}
.composer input { flex: 1; }
.composer button, .card button {
  cursor: pointer; background: var(--accent); border-color: var(--accent); color: #fff;
}
.composer button:disabled, .card button:disabled { opacity: 0.45; cursor: not-allowed; }
.composer select option:disabled { color: var(--dim); }
.card button { background: var(--card); color: var(--fg); border-color: var(--line); }
.card button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }

/* ── 知識庫 ───────────────────────────────────────── */
.card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 18px; }
.card + .card { margin-top: 20px; }
.card-head { display: flex; justify-content: space-between; align-items: center; }
.card h2 { font-size: 1rem; font-weight: 600; margin-bottom: 8px; }
.hint { color: var(--dim); font-size: 0.82rem; margin: 8px 0; }

.form { display: flex; flex-direction: column; gap: 10px; margin-top: 12px; max-width: 520px; }
.check { display: flex; align-items: center; gap: 8px; color: var(--dim); font-size: 0.85rem; }
.check input { width: auto; }

.job { margin-top: 16px; padding-top: 14px; border-top: 1px solid var(--line); }
.steps { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 10px; }
.step {
  font-size: 0.76rem; padding: 3px 10px; border-radius: 12px;
  background: var(--line); color: var(--dim);
}
.step.done { background: rgba(63, 191, 127, 0.16); color: var(--good); }
.step.now { background: var(--accent); color: #fff; }
.step.failed { opacity: 0.5; }

.state { color: var(--dim); font-size: 0.85rem; margin: 6px 0; }
.err { color: var(--bad); font-size: 0.85rem; margin: 6px 0; }
.warn { color: var(--warn); font-size: 0.82rem; margin: 6px 0; }

.kv { margin-top: 10px; font-size: 0.83rem; }
.kv > div { display: flex; gap: 10px; padding: 3px 0; }
.kv dt { color: var(--dim); min-width: 92px; }

.table { width: 100%; border-collapse: collapse; margin-top: 12px; font-size: 0.85rem; }
.table th, .table td { text-align: left; padding: 7px 10px; border-bottom: 1px solid var(--line); }
.table th { color: var(--dim); font-weight: 500; }
.table tfoot td { color: var(--dim); }
</style>
