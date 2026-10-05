{{- define "law-app.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "law-app.labels" -}}
app.kubernetes.io/name: {{ include "law-app.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
{{- end -}}

{{/* 元件的選擇器標籤。呼叫：include "law-app.selector" (dict "root" $ "component" "backend") */}}
{{- define "law-app.selector" -}}
app.kubernetes.io/name: {{ include "law-app.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/* 元件的資源名稱。Service 名稱也是它，nginx 與後端都靠這個名稱找人。 */}}
{{- define "law-app.fullname" -}}
{{- printf "%s-%s" .root.Release.Name .component | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "law-app.image" -}}
{{- printf "%s:%s" .repository .tag -}}
{{- end -}}

{{/* Secret 名稱：用既有的，否則用本 chart 建的。 */}}
{{- define "law-app.secretName" -}}
{{- if .Values.secrets.existingSecret -}}
{{- .Values.secrets.existingSecret -}}
{{- else -}}
{{- printf "%s-secrets" .Release.Name -}}
{{- end -}}
{{- end -}}
