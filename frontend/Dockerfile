# Build stage
FROM node:18-alpine AS builder
WORKDIR /app
COPY package*.json ./
# 建置階段需要 devDependencies（vite、@vitejs/plugin-vue 都在那裡），
# 不可加 --omit=dev。最終映像只留 dist/，這些相依不會進到執行階段。
# npm ci 依 package-lock.json 安裝；鎖檔不同步時退回 npm install。
RUN npm ci || npm install
COPY . .
RUN npm run build

# 以 /api 相對路徑呼叫後端，由下面的 nginx 代理到 law-backend:8000。
# 因此建置時不需要 VITE_BACKEND——那個變數只給本機 vite dev server 用。

# Production stage
# 版本要釘住：NGINX_ENTRYPOINT_LOCAL_RESOLVERS 需要 1.23 以上，
# 而 `latest` 類標籤上 k8s 重建 Pod 時可能拉到不同版本。
FROM nginx:1.27-alpine
COPY --from=builder /app/dist /usr/share/nginx/html

# 設定檔放在 templates/ 而不是 conf.d/：映像的啟動腳本會把它以環境變數
# 代換後寫成 /etc/nginx/conf.d/default.conf。見 nginx.conf 開頭的說明。
COPY nginx.conf /etc/nginx/templates/default.conf.template
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1
ENV BACKEND_URL=http://law-backend:8000

EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
