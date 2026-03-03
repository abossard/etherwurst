-- ClickHouse schema for Ethereum blockchain analytics
-- Applied to: clickhouse-ethereum-analytics service in ethereum namespace
-- Engine: ReplacingMergeTree (deduplicates on ORDER BY key)

CREATE TABLE IF NOT EXISTS blocks (
    number UInt64,
    hash String,
    parent_hash String,
    nonce String,
    sha3_uncles String,
    miner String,
    difficulty String,
    total_difficulty String,
    size UInt64,
    extra_data String,
    gas_limit UInt64,
    gas_used UInt64,
    timestamp DateTime,
    transaction_count UInt32,
    base_fee_per_gas UInt64,
    withdrawals_root String,
    blob_gas_used UInt64,
    excess_blob_gas UInt64
) ENGINE = ReplacingMergeTree()
ORDER BY number;

CREATE TABLE IF NOT EXISTS transactions (
    hash String,
    nonce UInt64,
    block_hash String,
    block_number UInt64,
    transaction_index UInt32,
    from_address String,
    to_address String,
    value String,
    gas UInt64,
    gas_price UInt64,
    input String,
    block_timestamp DateTime,
    receipt_cumulative_gas_used UInt64,
    receipt_gas_used UInt64,
    receipt_contract_address String,
    receipt_status UInt8,
    receipt_effective_gas_price UInt64,
    max_fee_per_gas UInt64,
    max_priority_fee_per_gas UInt64,
    transaction_type UInt8,
    max_fee_per_blob_gas UInt64,
    blob_versioned_hashes Array(String)
) ENGINE = ReplacingMergeTree()
ORDER BY (block_number, transaction_index);

CREATE TABLE IF NOT EXISTS logs (
    log_index UInt32,
    transaction_hash String,
    transaction_index UInt32,
    block_hash String,
    block_number UInt64,
    address String,
    data String,
    topics Array(String),
    block_timestamp DateTime
) ENGINE = ReplacingMergeTree()
ORDER BY (block_number, log_index);

CREATE TABLE IF NOT EXISTS etl_progress (
    dataset String,
    last_block UInt64,
    updated_at DateTime
) ENGINE = ReplacingMergeTree()
ORDER BY dataset;
