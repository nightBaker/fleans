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
Npgsql Maximum Pool Size of one workload (persistence.postgres.maxPoolSize.<workload>);
0 = Npgsql's default of 100. Usage: include "fleans.maxPoolSize" (dict "ctx" . "workload" "core")
*/}}
{{- define "fleans.maxPoolSize" -}}
{{- $size := int (index .ctx.Values.persistence.postgres.maxPoolSize .workload | default 0) -}}
{{- ternary 100 $size (eq $size 0) -}}
{{- end -}}

{{/*
Connection budget of the bundled Postgres (#910). Every Fleans process opens one Npgsql pool
per connection string: the `fleans` NpgsqlDataSource, one more when the Orleans AdoNet
clustering / reminders providers share that database (both open NpgsqlConnection with the same
string, so they share Npgsql's per-string pool), and the plugin host's Camunda database. Each
pool grows to its Maximum Pool
Size under burst, so the worst case
  Σ workloads  replicas (maxReplicas when autoscaled) × pools × maxPoolSize
must fit max_connections minus the 3 superuser-reserved slots, or Postgres answers
`53300: too many clients` and throughput collapses. Fails the render when it does not.
External servers (postgres.enabled=false) are not checked: the chart cannot see their limit.
*/}}
{{- define "fleans.postgresBudget" -}}
{{- if and .Values.postgres.enabled (eq (lower .Values.persistence.provider) "postgres") }}
{{- $shared := 1 }}
{{- $adoCluster := and (eq (lower .Values.clustering.provider) "postgres") (not .Values.clustering.postgres.existingSecret) }}
{{- $adoReminders := and (eq (lower .Values.reminders.provider) "postgres") (not .Values.reminders.postgres.existingSecret) }}
{{- if or $adoCluster $adoReminders }}{{ $shared = 2 }}{{ end }}
{{- $total := 0 }}
{{- $parts := list }}
{{- range $workload := list "core" "worker" "api" "web" "mcp" "plugins" }}
{{- $w := index $.Values $workload }}
{{- if or (eq $workload "core") $w.enabled }}
{{- $replicas := int $w.replicas }}
{{- if and $w.autoscaling $w.autoscaling.enabled }}{{ $replicas = int $w.autoscaling.maxReplicas }}{{ end }}
{{- $pools := $shared }}
{{- if eq $workload "plugins" }}{{ $pools = add $pools 1 }}{{ end }}
{{- $size := int (include "fleans.maxPoolSize" (dict "ctx" $ "workload" $workload)) }}
{{- $total = add $total (mul $replicas $pools $size) }}
{{- $parts = append $parts (printf "%s %d×%d×%d" $workload $replicas (int $pools) $size) }}
{{- end }}
{{- end }}
{{- $limit := sub (int .Values.postgres.maxConnections) 3 }}
{{- if gt (int $total) (int $limit) }}
{{- fail (printf "Npgsql pools can open %d connections to the bundled Postgres (replicas×pools×maxPoolSize: %s) but it allows %d (postgres.maxConnections %d minus 3 reserved). Raise postgres.maxConnections, lower persistence.postgres.maxPoolSize.*, or lower the replica counts." (int $total) (join ", " $parts) (int $limit) (int .Values.postgres.maxConnections)) }}
{{- end }}
{{- end }}
{{- end -}}

{{/*
Common env vars wired onto every fleans-* workload (core, worker, api, web, mcp, plugins).
Includes Redis, persistence, clustering, streaming, and any user-supplied extraEnv.
ASPNETCORE_URLS is intentionally excluded — each workload sets its own port.
Usage: include "fleans.commonEnv" (dict "ctx" . "workload" "core") — the workload picks its
persistence.postgres.maxPoolSize entry.
*/}}
{{- define "fleans.commonEnv" -}}
{{- $workload := .workload }}
{{- with .ctx }}
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
{{- /* Kubernetes expands $(VAR) from env defined earlier in the list, so the pool cap rides on the Secret's string. */}}
- name: FLEANS_POSTGRES_CONNECTION
  valueFrom:
    secretKeyRef:
      name: {{ include "fleans.postgres.secretName" . }}
      key: connection-string
- name: ConnectionStrings__fleans
  value: "$(FLEANS_POSTGRES_CONNECTION);Maximum Pool Size={{ include "fleans.maxPoolSize" (dict "ctx" . "workload" $workload) }}"
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
