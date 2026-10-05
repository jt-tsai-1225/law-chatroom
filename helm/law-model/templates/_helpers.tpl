{{- define "law-model.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "law-model.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s" (include "law-model.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "law-model.labels" -}}
app.kubernetes.io/name: {{ include "law-model.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
{{- end -}}

{{- define "law-model.selectorLabels" -}}
app.kubernetes.io/name: {{ include "law-model.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/*
權重 PVC 的名稱：使用者指定了既有 claim 就用那個，否則用本 chart 建的。
*/}}
{{- define "law-model.weightsClaim" -}}
{{- if .Values.model.weights.existingClaim -}}
{{- .Values.model.weights.existingClaim -}}
{{- else -}}
{{- printf "%s-weights" (include "law-model.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
引擎的資源名稱：<fullname>-<引擎鍵>，例如 law-model-cacheblend。
這個名稱同時是 Service 名稱，後端的 LlmEndpoints 就填它。
呼叫方式：include "law-model.engineName" (dict "root" $ "name" $key)
*/}}
{{- define "law-model.engineName" -}}
{{- printf "%s-%s" (include "law-model.fullname" .root) .name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
引擎的 PVC 名稱（權重）。perEngine 時每個引擎一個。
*/}}
{{- define "law-model.engineWeightsClaim" -}}
{{- if .root.Values.model.weights.existingClaim -}}
{{- .root.Values.model.weights.existingClaim -}}
{{- else if .root.Values.model.weights.perEngine -}}
{{- printf "%s-weights-%s" (include "law-model.fullname" .root) .name -}}
{{- else -}}
{{- include "law-model.weightsClaim" .root -}}
{{- end -}}
{{- end -}}
