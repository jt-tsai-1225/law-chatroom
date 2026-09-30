<script setup>
import { ref, nextTick, onMounted, computed, onUnmounted } from 'vue'
import axios from 'axios'

const API = import.meta.env.VITE_API_URL || '/api'

// ── 分頁 ────────────────────────────────────────────────────────
const tab = ref('chat')

// ══════════════════════════════════════════════════════════════
//  聊天
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

async function send() {
  const text = draft.value.trim()
  if (!text || sending.value) return

  messages.value.push({ role: 'user', content: text, at: new Date() })
  draft.value = ''
  sending.value = true
  await scrollDown()

  try {
    const { data } = await axios.post(`${API}/chat`, {
      message: text,
      chunkOrder: chunkOrder.value
    })

    messages.value.push({
      role: 'bot',
      content: data.reply,
      at: new Date(),
      stats: {
        ttft: data.ttftMilliseconds,
        total: data.totalMilliseconds,
        promptTokens: data.promptTokens,
        cachedTokens: data.cachedTokens,
        hitRate: data.cacheHitRate,
        articles: data.retrievedArticles || [],
        order: data.chunkOrderApplied,
        permutation: data.chunkPermutation || [],
        reordered: data.reorderedForCache,
        prefixCeiling: data.prefixOnlyCeilingTokens,
        overPrefix: data.cachedOverPrefixCeiling,
        tokensPerSecond: data.tokensPerSecond
      }
    })
  } catch (err) {
    messages.value.push({
      role: 'error',
      content: describeError(err),
      at: new Date()
    })
  } finally {
    sending.value = false
    await scrollDown()
  }
}

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

async function scrollDown() {
  await nextTick()
  if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
}

// ══════════════════════════════════════════════════════════════
//  知識庫
// ══════════════════════════════════════════════════════════════

const laws = ref([])
const totalChunks = ref(0)
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

const STAGE_ORDER = ['storing', 'parsing', 'embedding', 'warmup', 'done']

const stageIndex = computed(() => {
  if (!job.value) return -1
  return STAGE_ORDER.indexOf(job.value.stage)
})

async function loadKnowledgeBase() {
  kbLoading.value = true
  kbError.value = ''
  try {
    const { data } = await axios.get(`${API}/knowledgebase/documents`)
    laws.value = data.laws || []
    totalChunks.value = data.totalChunks || 0
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
  }
}

function fmtTime(d) {
  return new Date(d).toLocaleTimeString('zh-TW', { hour: '2-digit', minute: '2-digit' })
}

function fmtNum(n) {
  return typeof n === 'number' ? n.toLocaleString('zh-TW') : '—'
}

onMounted(() => { loadKnowledgeBase() })
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
    <section v-show="tab === 'chat'" class="panel">
      <div class="thread" ref="scroller">
        <p v-if="messages.length === 0" class="empty">
          問一個民法或公司法的問題，例如：<br />
          「公司重整時，董事拒絕移交帳冊文件，會被處以多少罰金？」
        </p>

        <div v-for="(m, i) in messages" :key="i" :class="['turn', m.role]">
          <div class="bubble">
            <p class="text">{{ m.content }}</p>

            <div v-if="m.stats" class="stats">
              <div class="row">
                <span class="k">TTFT</span>
                <span class="v strong">{{ fmtNum(m.stats.ttft) }} ms</span>
                <span class="k">總耗時</span>
                <span class="v">{{ fmtNum(m.stats.total) }} ms</span>
                <span class="k">生成速率</span>
                <span class="v">{{ m.stats.tokensPerSecond?.toFixed(1) }} tok/s</span>
              </div>

              <div class="row">
                <span class="k">KV 命中</span>
                <span class="v strong">
                  {{ fmtNum(m.stats.cachedTokens) }} / {{ fmtNum(m.stats.promptTokens) }}
                  （{{ m.stats.hitRate?.toFixed(2) }}%）
                </span>
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
                  <template v-if="m.stats.permutation.length">
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

      <div class="composer">
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
          {{ sending ? '查詢中…' : '送出' }}
        </button>
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
              v-for="(s, i) in STAGE_ORDER"
              :key="s"
              :class="['step', {
                done: stageIndex > i,
                now: job.stage === s,
                failed: job.stage === 'failed'
              }]"
            >{{ STAGE_TEXT[s] }}</span>
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
            <tr><th>法典</th><th>片段數</th><th>總字數</th><th>字數中位數</th><th>章節數</th><th></th></tr>
          </thead>
          <tbody>
            <tr v-for="l in laws" :key="l.law">
              <td>{{ l.law }}</td>
              <td>{{ fmtNum(l.chunks) }}</td>
              <td>{{ fmtNum(l.totalChars) }}</td>
              <td>{{ fmtNum(l.medianChars) }}</td>
              <td>{{ fmtNum(l.chapters) }}</td>
              <td><button @click="runWarmup(l.law)" :disabled="warming">預熱</button></td>
            </tr>
          </tbody>
          <tfoot>
            <tr>
              <td>合計</td>
              <td>{{ fmtNum(totalChunks) }}</td>
              <td colspan="3"></td>
              <td><button @click="runWarmup(null)" :disabled="warming">全部預熱</button></td>
            </tr>
          </tfoot>
        </table>

        <!--
          這段說明必須留著。L2 的索引存在記憶體，服務重啟後磁碟上的 KV
          檔案還在、內容也完整，但讀不到——預熱是每次啟動都要重跑的動作，
          不是一次性的資料準備。不寫清楚很容易誤以為已經永久生效。
        -->
        <p class="hint">
          KV 預熱讓片段的 KV 先進入快取，第一位使用者就不必承受冷啟成本。
          <strong>服務重啟後 L2 索引不會重建，需要重新預熱。</strong>
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

.app { max-width: 1000px; margin: 0 auto; height: 100vh; display: flex; flex-direction: column; }

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
.note { color: var(--good); font-size: 0.76rem; margin: 2px 0 6px; }
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
