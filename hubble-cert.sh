#!/usr/bin/env bash
# Hubble CLI wrapper that auto-downloads hubble v0.13.0 and configures mTLS per cluster context.
# Usage: ./hubble-cert.sh observe --last 20
#   or:  alias hubble='./hubble-cert.sh' && hubble observe --last 20

set -euo pipefail

HUBBLE_VERSION="v0.13.0"
CACHE_DIR="${TMPDIR:-/tmp}/hubble-cache"
HUBBLE_BIN="${CACHE_DIR}/hubble-${HUBBLE_VERSION}"

# Download hubble binary if not present
if [ ! -x "${HUBBLE_BIN}" ]; then
  echo "Downloading hubble ${HUBBLE_VERSION}..." >&2
  mkdir -p "${CACHE_DIR}"
  ARCH=$(uname -m); case "${ARCH}" in x86_64) ARCH="amd64";; aarch64|arm64) ARCH="arm64";; esac
  OS=$(uname -s | tr '[:upper:]' '[:lower:]')
  curl -sL "https://github.com/cilium/hubble/releases/download/${HUBBLE_VERSION}/hubble-${OS}-${ARCH}.tar.gz" | tar -xz -C "${CACHE_DIR}"
  mv "${CACHE_DIR}/hubble" "${HUBBLE_BIN}"
  chmod +x "${HUBBLE_BIN}"
  echo "Cached at ${HUBBLE_BIN}" >&2
fi

# Fetch certs per kube context
CLUSTER_NAME=$(kubectl config current-context 2>/dev/null || echo "unknown")
CERT_DIR="${CACHE_DIR}/certs/${CLUSTER_NAME}"

if [ ! -f "${CERT_DIR}/ca.crt" ] || [ ! -f "${CERT_DIR}/tls.crt" ] || [ ! -f "${CERT_DIR}/tls.key" ]; then
  echo "Fetching hubble-relay client certs for '${CLUSTER_NAME}'..." >&2
  mkdir -p "${CERT_DIR}"
  kubectl get secret hubble-relay-client-certs -n kube-system -o jsonpath='{.data.ca\.crt}' | base64 -d > "${CERT_DIR}/ca.crt"
  kubectl get secret hubble-relay-client-certs -n kube-system -o jsonpath='{.data.tls\.crt}' | base64 -d > "${CERT_DIR}/tls.crt"
  kubectl get secret hubble-relay-client-certs -n kube-system -o jsonpath='{.data.tls\.key}' | base64 -d > "${CERT_DIR}/tls.key"
  chmod 600 "${CERT_DIR}/tls.key"
  echo "Certs cached in ${CERT_DIR}" >&2
fi

# Configure hubble for the current context
"${HUBBLE_BIN}" config set tls true
"${HUBBLE_BIN}" config set tls-ca-cert-files "${CERT_DIR}/ca.crt"
"${HUBBLE_BIN}" config set tls-client-cert-file "${CERT_DIR}/tls.crt"
"${HUBBLE_BIN}" config set tls-client-key-file "${CERT_DIR}/tls.key"
"${HUBBLE_BIN}" config set tls-server-name "*.hubble-relay.cilium.io"

exec "${HUBBLE_BIN}" "$@"
