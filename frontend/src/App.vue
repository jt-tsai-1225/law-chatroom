<template>
  <div class="chat-container">
    <div class="chat-header">
      <h1>⚖️ 法律諮詢聊天機器人</h1>
      <p class="subtitle">民法・公司法 諮詢助手</p>
    </div>

    <div class="chat-messages" ref="messagesContainer">
      <div v-for="(msg, index) in messages" :key="index" 
           :class="['message', msg.role === 'user' ? 'user-message' : 'bot-message']">
        <div class="message-content">
          <strong>{{ msg.role === 'user' ? '您' : '助手' }}:</strong>
          <p>{{ msg.content }}</p>
        </div>
        <span class="message-time">{{ formatTime(msg.timestamp) }}</span>
      </div>
    </div>

    <div class="chat-input">
      <input
        v-model="newMessage"
        @keyup.enter="sendMessage"
        placeholder="請輸入您的法律問題..."
        :disabled="loading"
      />
      <button @click="sendMessage" :disabled="!newMessage.trim() || loading">
        <span v-if="loading" class="spinner"></span>
        <span v-else>送出</span>
      </button>
    </div>
  </div>
</template>

<script setup>
import { ref, nextTick, onMounted } from 'vue'
import axios from 'axios'

const messages = ref([
  {
    role: 'bot',
    content: '您好！我是法律諮詢助手，專注於中華民國民法與公司法領域，請問有什麼問題想要諮詢？',
    timestamp: new Date()
  }
])
const newMessage = ref('')
const loading = ref(false)
const messagesContainer = ref(null)

const API_URL = import.meta.env.VITE_API_URL || '/api'

async function sendMessage() {
  if (!newMessage.value.trim() || loading.value) return

  const userMsg = {
    role: 'user',
    content: newMessage.value,
    timestamp: new Date()
  }
  
  messages.value.push(userMsg)
  const messageToSend = newMessage.value
  newMessage.value = ''
  loading.value = true

  try {
    const response = await axios.post(`${API_URL}/chat`, {
      message: messageToSend
    })
    
    messages.value.push({
      role: 'bot',
      content: response.data.reply,
      timestamp: new Date()
    })
  } catch (error) {
    console.error('Error sending message:', error)
    messages.value.push({
      role: 'bot',
      content: '抱歉，發生錯誤，請稍後再試一次。',
      timestamp: new Date()
    })
  } finally {
    loading.value = false
    await nextTick()
    scrollToBottom()
  }
}

function formatTime(timestamp) {
  return new Date(timestamp).toLocaleTimeString([], { 
    hour: '2-digit', 
    minute: '2-digit' 
  })
}

function scrollToBottom() {
  if (messagesContainer.value) {
    messagesContainer.value.scrollTop = messagesContainer.value.scrollHeight
  }
}

// Scroll to bottom on mount
onMounted(() => {
  scrollToBottom()
})
</script>

<style>
* {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  min-height: 100vh;
}

.chat-container {
  max-width: 800px;
  margin: 0 auto;
  height: 100vh;
  display: flex;
  flex-direction: column;
  background: rgba(255, 255, 255, 0.95);
  border-radius: 20px;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.3);
  overflow: hidden;
}

.chat-header {
  padding: 20px;
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
  text-align: center;
}

.chat-header h1 {
  font-size: 1.5rem;
}

.subtitle {
  font-size: 0.85rem;
  opacity: 0.8;
  margin-top: 5px;
}

.chat-messages {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
}

.message {
  margin-bottom: 15px;
  display: flex;
  flex-direction: column;
}

.user-message {
  align-items: flex-end;
}

.bot-message {
  align-items: flex-start;
}

.message-content {
  max-width: 80%;
  padding: 12px 16px;
  border-radius: 18px;
}

.user-message .message-content {
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
  border-bottom-right-radius: 4px;
}

.bot-message .message-content {
  background: #f0f0f0;
  color: #333;
  border-bottom-left-radius: 4px;
}

.message-content p {
  margin-top: 5px;
  line-height: 1.5;
}

.message-time {
  font-size: 0.75rem;
  color: #888;
  margin-top: 4px;
  padding: 0 10px;
}

.chat-input {
  display: flex;
  padding: 15px;
  background: white;
  border-top: 1px solid #eee;
}

.chat-input input {
  flex: 1;
  padding: 12px 16px;
  border: 2px solid #e0e0e0;
  border-radius: 25px;
  font-size: 1rem;
  outline: none;
  transition: border-color 0.3s;
}

.chat-input input:focus {
  border-color: #667eea;
}

.chat-input button {
  margin-left: 10px;
  padding: 12px 24px;
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
  border: none;
  border-radius: 25px;
  cursor: pointer;
  font-size: 1rem;
  transition: opacity 0.3s;
}

.chat-input button:hover:not(:disabled) {
  opacity: 0.9;
}

.chat-input button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.spinner {
  display: inline-block;
  width: 16px;
  height: 16px;
  border: 2px solid rgba(255,255,255,0.3);
  border-top-color: white;
  border-radius: 50%;
  animation: spin 0.6s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

/* Scrollbar styling */
.chat-messages::-webkit-scrollbar {
  width: 6px;
}

.chat-messages::-webkit-scrollbar-track {
  background: transparent;
}

.chat-messages::-webkit-scrollbar-thumb {
  background: #ccc;
  border-radius: 3px;
}
</style>
