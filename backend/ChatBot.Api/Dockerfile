# ───────────────────────────────────────────────────────────────
# Build stage
# ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先只複製專案檔做還原，再複製其餘原始碼。
# 這樣改一行 code 不會讓 NuGet 還原那一層失效，重建時省去重抓全部套件。
COPY *.csproj ./
RUN dotnet restore --disable-parallel

COPY . .
RUN dotnet publish -c release -o /app/publish --no-restore

# ───────────────────────────────────────────────────────────────
# Runtime stage
# ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# 法規解析在 Python。
#
# 為什麼後端映像要裝 Python：條文切分曾經整組失效過——1,972 條條文被切成
# 270 個標籤錯亂的片段，而且靜默發生，TTFT 與命中率都正常，只有答案引用了
# 不存在的條號。修好的實作在 Python 且經過實測（327 片段、0 重複標籤）。
# 在 C# 重寫意味著換一個 PDF 文字引擎，斷行結果未必相同，而條號比對是
# 整行比對——斷行一變，切分再次失效，同樣靜默。
#
# --break-system-packages：Debian 的 Python 受 PEP 668 保護，
# 容器內沒有共用該直譯器的其他程式，直接裝進系統環境是可接受的。
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip \
    && pip3 install --no-cache-dir --break-system-packages pymupdf \
    && apt-get purge -y python3-pip \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# tools/parse_pdf.py 由 csproj 的 <Content Include="tools/**"> 帶進 publish 輸出，
# 因此隨這一行一起進到 /app/tools/，不需要額外的 COPY。
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000

ENTRYPOINT ["dotnet", "ChatBot.Api.dll"]
