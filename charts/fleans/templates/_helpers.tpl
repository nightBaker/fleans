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
Orleans ClusterId — clustering.clusterId, or the release's full name.
*/}}
{{- define "fleans.clusterId" -}}
{{- default (include "fleans.fullname" .) .Values.clustering.clusterId -}}
{{- end -}}

{{/*
Silo Deployment name: "<role>-<fullname>". With Kubernetes hosting the pod name
becomes the Orleans silo name, and Fleans placement reads the silo role from
its prefix (core- / worker- / plugin-), so the pod name — derived from the Deployment
name — must start with the role.
Usage: include "fleans.siloDeploymentName" (dict "ctx" . "role" "core" "suffix" "")
*/}}
{{- define "fleans.siloDeploymentName" -}}
{{- printf "%s-%s%s" .role (include "fleans.fullname" .ctx) .suffix | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
ServiceAccount the silo pods run as (Kubernetes hosting needs pod read/watch/patch).
*/}}
{{- define "fleans.siloServiceAccountName" -}}
{{- printf "%s-silo" (include "fleans.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Pod labels Orleans Kubernetes hosting selects silo pods by. Applied to every
silo pod template (core, worker, plugin) — not to the selector.
*/}}
{{- define "fleans.siloPodLabels" -}}
orleans/serviceId: {{ .Values.clustering.serviceId | quote }}
orleans/clusterId: {{ include "fleans.clusterId" . | quote }}
{{- end -}}

{{/*
Pod-spec fields shared by every silo pod: the ServiceAccount (Kubernetes
hosting) and a termination grace period long enough for graceful silo
shutdown (leave membership, hand off grains and the reminder ring).
*/}}
{{- define "fleans.siloPodSpec" -}}
terminationGracePeriodSeconds: {{ .Values.clustering.terminationGracePeriodSeconds }}
{{- if .Values.clustering.kubernetes.enabled }}
serviceAccountName: {{ include "fleans.siloServiceAccountName" . }}
{{- end }}
{{- end -}}

{{/*
Env vars only Orleans silos need (core, worker, plugin): Kubernetes
hosting + downward API and the host shutdown timeout. Streams are implicit-only,
so there is no PubSubStore grain storage (#911).
*/}}
{{- define "fleans.siloEnv" -}}
- name: Fleans__Kubernetes__Enabled
  value: {{ .Values.clustering.kubernetes.enabled | quote }}
{{- if .Values.clustering.kubernetes.enabled }}
- name: POD_NAME
  valueFrom:
    fieldRef:
      fieldPath: metadata.name
- name: POD_NAMESPACE
  valueFrom:
    fieldRef:
      fieldPath: metadata.namespace
- name: POD_IP
  valueFrom:
    fieldRef:
      fieldPath: status.podIP
{{- end }}
{{- $grace := int .Values.clustering.terminationGracePeriodSeconds }}
{{- if lt $grace 15 }}
{{- fail "clustering.terminationGracePeriodSeconds must be at least 15 (the silo needs time to leave the cluster gracefully)." }}
{{- end }}
{{- /* .NET HostOptions.ShutdownTimeout: finish the graceful silo stop 10 s before SIGKILL. */}}
- name: shutdownTimeoutSeconds
  value: {{ sub $grace 10 | quote }}
{{- if .Values.loadShedding.enabled }}
- name: Fleans__LoadShedding__Enabled
  value: "true"
- name: Fleans__LoadShedding__CpuThreshold
  value: {{ .Values.loadShedding.cpuThreshold | quote }}
{{- end }}
{{- end -}}

{{/*
JWT issuer for the HTTP hosts that validate bearer tokens (core, api, mcp). Fleans.Api and
Fleans.Mcp enable auth iff Authentication__Authority is non-empty; each defaults its own
audience (fleans-api / fleans-mcp). Web stamps Authority itself alongside its OIDC client.
*/}}
{{- define "fleans.authEnv" -}}
{{- if .Values.auth.authority }}
- name: Authentication__Authority
  value: {{ .Values.auth.authority | quote }}
{{- end }}
{{- end -}}

{{/*
Common env vars wired onto every fleans-* workload (api Core, api Worker, web,
mcp). Includes Redis, persistence, clustering, streaming, and any user-supplied
extraEnv. ASPNETCORE_URLS is intentionally excluded — each workload sets its own port.
*/}}
{{- define "fleans.commonEnv" -}}
{{- if .Values.redis.enabled }}
- name: ConnectionStrings__orleans-redis
  value: "{{ include "fleans.fullname" . }}-redis:{{ .Values.redis.service.port }}"
{{- end }}
- name: Orleans__ServiceId
  value: {{ .Values.clustering.serviceId | quote }}
- name: Orleans__ClusterId
  value: {{ include "fleans.clusterId" . | quote }}
{{- $clustering := lower .Values.clustering.provider }}
{{- if not (has $clustering (list "redis" "postgres")) }}
{{- fail (printf "Unsupported clustering.provider %q. Valid: Redis, Postgres (case-insensitive)." .Values.clustering.provider) }}
{{- end }}
{{- if and (eq $clustering "postgres") (ne (lower .Values.persistence.provider) "postgres") }}
{{- fail "clustering.provider=Postgres requires persistence.provider=Postgres." }}
{{- end }}
- name: Fleans__Clustering__Provider
  value: {{ ternary "Postgres" "Redis" (eq $clustering "postgres") | quote }}
{{- if and (eq $clustering "postgres") .Values.clustering.postgres.existingSecret }}
- name: ConnectionStrings__fleans-cluster
  valueFrom:
    secretKeyRef:
      name: {{ .Values.clustering.postgres.existingSecret | quote }}
      key: {{ .Values.clustering.postgres.existingSecretKey | quote }}
{{- end }}
{{- if not (has (lower .Values.persistence.provider) (list "sqlite" "postgres")) }}
{{- fail (printf "Unsupported persistence.provider %q. Valid: Sqlite, Postgres (case-insensitive)." .Values.persistence.provider) }}
{{- end }}
- name: Persistence__Provider
  value: {{ .Values.persistence.provider | quote }}
{{- if and (eq (lower .Values.persistence.provider) "postgres") .Values.postgres.enabled }}
- name: ConnectionStrings__fleans
  valueFrom:
    secretKeyRef:
      name: {{ include "fleans.postgres.secretName" . }}
      key: connection-string
{{- end }}
{{- $reminders := lower .Values.reminders.provider }}
{{- if not (has $reminders (list "redis" "postgres")) }}
{{- fail (printf "Unsupported reminders.provider %q. Valid: Redis, Postgres (case-insensitive)." .Values.reminders.provider) }}
{{- end }}
{{- if and (eq $reminders "postgres") (ne (lower .Values.persistence.provider) "postgres") }}
{{- fail "reminders.provider=Postgres requires persistence.provider=Postgres." }}
{{- end }}
- name: Fleans__Reminders__Provider
  value: {{ ternary "Postgres" "Redis" (eq $reminders "postgres") | quote }}
{{- if and (eq $reminders "postgres") .Values.reminders.postgres.existingSecret }}
- name: ConnectionStrings__fleans-reminders
  valueFrom:
    secretKeyRef:
      name: {{ .Values.reminders.postgres.existingSecret | quote }}
      key: {{ .Values.reminders.postgres.existingSecretKey | quote }}
{{- end }}
{{- $provider := lower .Values.streaming.provider }}
{{- if not (has $provider (list "memory" "redis" "kafka" "azurequeue")) }}
{{- fail (printf "Unsupported streaming.provider %q. Valid: Memory, Redis, Kafka, AzureQueue (case-insensitive)." .Values.streaming.provider) }}
{{- end }}
{{- if eq $provider "memory" }}
- name: Fleans__Streaming__Provider
  value: "Memory"
{{- else if eq $provider "redis" }}
- name: Fleans__Streaming__Provider
  value: "Redis"
- name: Fleans__Streaming__Redis__TotalQueueCount
  value: {{ .Values.streaming.redis.totalQueueCount | quote }}
{{- else if eq $provider "kafka" }}
- name: Fleans__Streaming__Provider
  value: "Kafka"
- name: Fleans__Streaming__Kafka__Brokers
  value: {{ .Values.streaming.kafka.brokers | quote }}
- name: Fleans__Streaming__Kafka__QueueCount
  value: {{ .Values.streaming.kafka.queueCount | quote }}
{{- include "fleans.kafkaSecurityEnv" . }}
{{- else if eq $provider "azurequeue" }}
- name: Fleans__Streaming__Provider
  value: "AzureQueue"
- name: Fleans__Streaming__AzureQueue__ConnectionString
  value: {{ .Values.streaming.azureQueue.connectionString | quote }}
{{- end }}
{{- with .Values.extraEnv }}
{{ toYaml . }}
{{- end }}
{{- end -}}


{{/*
Kafka SASL / TLS env (streaming.kafka.security), emitted in the Kafka branch of
fleans.commonEnv. The engine validates the combination at silo startup; the
template only rejects what it can see here (an unknown protocol, TLS files
without a TLS protocol).
*/}}
{{- define "fleans.kafkaSecurityEnv" -}}
{{- $sec := .Values.streaming.kafka.security }}
{{- if $sec.protocol }}
{{- if not (has (lower $sec.protocol) (list "plaintext" "ssl" "saslplaintext" "saslssl")) }}
{{- fail (printf "Unsupported streaming.kafka.security.protocol %q. Valid: Plaintext, Ssl, SaslPlaintext, SaslSsl." $sec.protocol) }}
{{- end }}
- name: Fleans__Streaming__Kafka__SecurityProtocol
  value: {{ $sec.protocol | quote }}
{{- end }}
{{- if $sec.saslMechanism }}
- name: Fleans__Streaming__Kafka__SaslMechanism
  value: {{ $sec.saslMechanism | quote }}
{{- end }}
{{- if $sec.saslUsername }}
- name: Fleans__Streaming__Kafka__SaslUsername
  value: {{ $sec.saslUsername | quote }}
{{- end }}
{{- if $sec.saslPasswordSecret }}
- name: Fleans__Streaming__Kafka__SaslPassword
  valueFrom:
    secretKeyRef:
      name: {{ $sec.saslPasswordSecret | quote }}
      key: {{ $sec.saslPasswordSecretKey | quote }}
{{- end }}
{{- if $sec.tlsSecret }}
{{- if not (has (lower $sec.protocol) (list "ssl" "saslssl")) }}
{{- fail "streaming.kafka.security.tlsSecret requires streaming.kafka.security.protocol Ssl or SaslSsl." }}
{{- end }}
- name: Fleans__Streaming__Kafka__SslCaLocation
  value: "/etc/fleans/kafka/ca.crt"
{{- if $sec.clientCertificate }}
- name: Fleans__Streaming__Kafka__SslCertificateLocation
  value: "/etc/fleans/kafka/tls.crt"
- name: Fleans__Streaming__Kafka__SslKeyLocation
  value: "/etc/fleans/kafka/tls.key"
{{- end }}
{{- end }}
{{- end -}}

{{/*
Mounts streaming.kafka.security.tlsSecret at /etc/fleans/kafka, the directory
fleans.kafkaSecurityEnv points the Ssl*Location settings at. Both render
nothing unless the Kafka provider is selected and tlsSecret is set; every
Deployment that includes fleans.commonEnv includes both.
*/}}
{{- define "fleans.kafkaTlsVolumeMounts" -}}
{{- if and (eq (lower .Values.streaming.provider) "kafka") .Values.streaming.kafka.security.tlsSecret }}
volumeMounts:
  - name: kafka-tls
    mountPath: /etc/fleans/kafka
    readOnly: true
{{- end }}
{{- end -}}

{{- define "fleans.kafkaTlsVolumes" -}}
{{- if and (eq (lower .Values.streaming.provider) "kafka") .Values.streaming.kafka.security.tlsSecret }}
volumes:
  - name: kafka-tls
    secret:
      secretName: {{ .Values.streaming.kafka.security.tlsSecret | quote }}
{{- end }}
{{- end -}}
