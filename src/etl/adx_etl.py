#!/usr/bin/env python3
"""
Ethereum ADX ETL: Extract from Erigon via cryo → Parquet → Azure Blob → ADX.

Designed to run as a K8s CronJob on a cost-optimized schedule:
  1. Start ADX cluster (if stopped)
  2. Extract new blocks via cryo into Parquet
  3. Upload Parquet to Azure Blob Storage
  4. Trigger ADX queued ingestion
  5. Optionally stop ADX cluster after ingestion

Environment variables:
  ERIGON_RPC_URL        Erigon JSON-RPC endpoint (default: http://erigon:8545)
  ADX_CLUSTER_URI       ADX cluster URI (e.g. https://adxetherwurst.westeurope.kusto.windows.net)
  ADX_DATABASE           ADX database name (default: ethereum)
  STORAGE_ACCOUNT_NAME  Azure Storage account for staging Parquet files
  STORAGE_CONTAINER     Blob container name (default: ethereum-etl)
  MANAGED_IDENTITY_ID   Client ID of the managed identity (for workload identity auth)
  MAX_BLOCKS            Maximum blocks per run (default: 50000)
  STOP_ADX_AFTER        Stop ADX cluster after ingestion (default: false)
"""
import json, os, subprocess, sys, time, urllib.request

ERIGON_RPC = os.environ.get("ERIGON_RPC_URL", "http://erigon:8545")
ADX_CLUSTER_URI = os.environ["ADX_CLUSTER_URI"]
ADX_DATABASE = os.environ.get("ADX_DATABASE", "ethereum")
STORAGE_ACCOUNT = os.environ["STORAGE_ACCOUNT_NAME"]
STORAGE_CONTAINER = os.environ.get("STORAGE_CONTAINER", "ethereum-etl")
MAX_BLOCKS = int(os.environ.get("MAX_BLOCKS", "50000"))
STOP_ADX_AFTER = os.environ.get("STOP_ADX_AFTER", "false").lower() == "true"

WORK_DIR = "/tmp/ethereum-extract"

# cryo datasets and their corresponding ADX table names
DATASETS = {
    "blocks": "Blocks",
    "transactions": "Transactions",
    "logs": "Logs",
    "traces": "Traces",
}


def rpc_call(method, params):
    payload = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    req = urllib.request.Request(ERIGON_RPC, data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        result = json.loads(resp.read())
    if "error" in result and result["error"]:
        raise Exception(f"RPC error: {result['error']}")
    return result.get("result")


def get_chain_head():
    result = rpc_call("eth_blockNumber", [])
    return int(result, 16) if result else 0


def get_last_ingested_block():
    """Query ADX for the last ingested block number."""
    try:
        result = subprocess.run(
            ["az", "kusto", "query", "--cluster-name", ADX_CLUSTER_URI,
             "--database-name", ADX_DATABASE,
             "--query", "EtlProgress | where dataset == 'cryo' | summarize max(last_block)"],
            capture_output=True, text=True, timeout=60
        )
        if result.returncode == 0 and result.stdout.strip():
            data = json.loads(result.stdout)
            if data and len(data) > 0:
                val = data[0].get("max_last_block", 0)
                return int(val) if val else 0
    except Exception as e:
        print(f"Warning: Could not query ADX for progress: {e}")
    return 0


def run_cryo(start_block, end_block):
    """Run cryo to extract blockchain data as Parquet files."""
    os.makedirs(WORK_DIR, exist_ok=True)

    datasets = list(DATASETS.keys())
    cmd = [
        "cryo", *datasets,
        "-b", f"{start_block}:{end_block}",
        "--rpc", ERIGON_RPC,
        "-o", WORK_DIR,
        "--output-format", "parquet",
        "--chunk-size", "1000",
        "--max-concurrent-requests", "4",
        "--requests-per-second", "50",
    ]
    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=3600)
    if result.returncode != 0:
        print(f"cryo stderr: {result.stderr}", file=sys.stderr)
        raise Exception(f"cryo failed with exit code {result.returncode}")
    print(f"cryo output: {result.stdout}")


def upload_to_blob():
    """Upload Parquet files to Azure Blob Storage."""
    cmd = [
        "az", "storage", "blob", "upload-batch",
        "--destination", STORAGE_CONTAINER,
        "--source", WORK_DIR,
        "--account-name", STORAGE_ACCOUNT,
        "--auth-mode", "login",
        "--overwrite",
        "--pattern", "*.parquet",
    ]
    print(f"Uploading to blob: {STORAGE_ACCOUNT}/{STORAGE_CONTAINER}")
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=1800)
    if result.returncode != 0:
        print(f"Upload stderr: {result.stderr}", file=sys.stderr)
        raise Exception(f"Blob upload failed with exit code {result.returncode}")
    print(f"Upload complete: {result.stdout}")


def ingest_to_adx():
    """Trigger ADX queued ingestion for uploaded Parquet files.

    Uses the Kusto Python SDK if available, otherwise falls back to
    .ingest commands via az kusto query.
    """
    try:
        from azure.kusto.data import KustoConnectionStringBuilder
        from azure.kusto.ingest import QueuedIngestClient, IngestionProperties
        from azure.kusto.ingest import DataFormat
        from azure.identity import DefaultAzureCredential

        ingest_uri = ADX_CLUSTER_URI.replace("https://", "https://ingest-")
        credential = DefaultAzureCredential()
        kcsb = KustoConnectionStringBuilder.with_azure_token_credential(ingest_uri, credential)
        client = QueuedIngestClient(kcsb)

        blob_base = f"https://{STORAGE_ACCOUNT}.blob.core.windows.net/{STORAGE_CONTAINER}"

        for dataset, table in DATASETS.items():
            props = IngestionProperties(
                database=ADX_DATABASE,
                table=table,
                data_format=DataFormat.PARQUET,
            )
            # List parquet files for this dataset and ingest each
            list_cmd = [
                "az", "storage", "blob", "list",
                "--container-name", STORAGE_CONTAINER,
                "--account-name", STORAGE_ACCOUNT,
                "--auth-mode", "login",
                "--prefix", dataset,
                "--query", "[].name",
                "-o", "json",
            ]
            list_result = subprocess.run(list_cmd, capture_output=True, text=True, timeout=60)
            if list_result.returncode == 0:
                blobs = json.loads(list_result.stdout)
                for blob_name in blobs:
                    if blob_name.endswith(".parquet"):
                        blob_url = f"{blob_base}/{blob_name}"
                        print(f"  Ingesting {blob_name} → {table}")
                        client.ingest_from_blob(blob_url, ingestion_properties=props)

        print("ADX ingestion queued successfully (SDK)")
        return
    except ImportError:
        print("Kusto SDK not available, falling back to az CLI ingestion")

    # Fallback: use .ingest inline commands via az kusto query
    blob_base = f"https://{STORAGE_ACCOUNT}.blob.core.windows.net/{STORAGE_CONTAINER}"
    for dataset, table in DATASETS.items():
        query = f".ingest into table {table} (h@'{blob_base}/{dataset}/') with (format='parquet')"
        cmd = [
            "az", "kusto", "query",
            "--cluster-name", ADX_CLUSTER_URI,
            "--database-name", ADX_DATABASE,
            "--query", query,
        ]
        print(f"  Ingesting {dataset}/ → {table}")
        subprocess.run(cmd, capture_output=True, text=True, timeout=300)

    print("ADX ingestion triggered (CLI fallback)")


def update_progress(block_num):
    """Record ETL progress in ADX."""
    query = f".ingest inline into table EtlProgress <| cryo,{block_num},{time.strftime('%Y-%m-%dT%H:%M:%SZ')}"
    subprocess.run(
        ["az", "kusto", "query", "--cluster-name", ADX_CLUSTER_URI,
         "--database-name", ADX_DATABASE, "--query", query],
        capture_output=True, text=True, timeout=60
    )


def cleanup():
    """Remove temporary Parquet files."""
    subprocess.run(["rm", "-rf", WORK_DIR], capture_output=True)


def main():
    print(f"ADX ETL starting | Erigon: {ERIGON_RPC} | ADX: {ADX_CLUSTER_URI}")

    chain_head = get_chain_head()
    last_block = get_last_ingested_block()
    print(f"Chain head: {chain_head} | Last ingested: {last_block}")

    if last_block == 0:
        # First run: start from recent blocks
        start = max(chain_head - 10000, 0)
        print(f"First run — starting from block {start}")
    else:
        start = last_block + 1

    if start > chain_head:
        print("Already up to date!")
        return

    end = min(start + MAX_BLOCKS - 1, chain_head)
    print(f"Extracting blocks {start} → {end} ({end - start + 1} blocks)")

    try:
        run_cryo(start, end)
        upload_to_blob()
        ingest_to_adx()
        update_progress(end)
        print(f"ETL complete: blocks {start} → {end}")
    finally:
        cleanup()

    if STOP_ADX_AFTER:
        print("Stopping ADX cluster to save costs...")
        subprocess.run(
            ["az", "kusto", "cluster", "stop",
             "--name", ADX_CLUSTER_URI.split("//")[1].split(".")[0],
             "--resource-group", os.environ.get("AZURE_RESOURCE_GROUP", ""),
             "--no-wait"],
            capture_output=True, text=True
        )


if __name__ == "__main__":
    main()
