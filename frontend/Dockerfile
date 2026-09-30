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
FROM nginx:alpine
COPY --from=builder /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
