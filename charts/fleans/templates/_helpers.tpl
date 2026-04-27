{{/*
Expand the name of the chart.
*/}}
{{- define "fleans.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Fully-qualified app name.
*/}}
{{- define "fleans.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
Chart label.
*/}}
{{- define "fleans.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Common labels applied to every resource.
*/}}
{{- define "fleans.labels" -}}
helm.sh/chart: {{ include "fleans.chart" . }}
app.kubernetes.io/name: {{ include "fleans.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: fleans
{{- end -}}

{{/*
Selector labels for a single workload component.
Usage: include "fleans.selectorLabels" (dict "ctx" . "component" "core")
*/}}
{{- define "fleans.selectorLabels" -}}
app.kubernetes.io/name: {{ include "fleans.name" .ctx }}
app.kubernetes.io/instance: {{ .ctx.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/*
Image tag — fall back to chart appVersion when image.tag is unset.
*/}}
{{- define "fleans.imageTag" -}}
{{- default .Chart.AppVersion .Values.image.tag -}}
{{- end -}}

{{/*
Resolve a workload's image reference.
Usage: include "fleans.imageRef" (dict "ctx" . "repo" .Values.image.api.repository)
*/}}
{{- define "fleans.imageRef" -}}
{{- printf "%s:%s" .repo (include "fleans.imageTag" .ctx) -}}
{{- end -}}

{{/*
Postgres-password Secret name. Returns existingSecret when set, otherwise
the chart-managed Secret name.
*/}}
{{- define "fleans.postgres.secretName" -}}
{{- if .Values.postgres.existingSecret -}}
{{- .Values.postgres.existingSecret -}}
{{- else -}}
{{- printf "%s-postgres" (include "fleans.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
OIDC client-secret Secret name. Returns existingSecret when set, otherwise
the chart-managed Secret name.
*/}}
{{- define "fleans.auth.secretName" -}}
{{- if .Values.auth.clientSecretExistingSecret -}}
{{- .Values.auth.clientSecretExistingSecret -}}
{{- else -}}
{{- printf "%s-oidc" (include "fleans.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
Common env vars wired onto every fleans-* workload (api Core, api Worker, web,
mcp). Includes Redis, persistence, streaming, and any user-supplied extraEnv.
ASPNETCORE_URLS is intentionally excluded — each workload sets its own port.
*/}}
{{- define "fleans.commonEnv" -}}
- name: ConnectionStrings__orleans-redis
  value: "{{ include "fleans.fullname" . }}-redis:{{ .Values.redis.service.port }}"
- name: Persistence__Provider
  value: {{ .Values.persistence.provider | quote }}
{{- if eq (lower .Values.persistence.provider) "postgres" }}
- name: ConnectionStrings__fleans
  valueFrom:
    secretKeyRef:
      name: {{ include "fleans.postgres.secretName" . }}
      key: connection-string
{{- end }}
{{- if eq (lower .Values.streaming.provider) "kafka" }}
- name: Fleans__Streaming__Provider
  value: "Kafka"
- name: Fleans__Streaming__Kafka__Brokers
  value: {{ .Values.streaming.kafka.brokers | quote }}
{{- end }}
{{- with .Values.extraEnv }}
{{ toYaml . }}
{{- end }}
{{- end -}}
