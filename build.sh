#!/usr/bin/env bash
set -e

TARGET="${1:-all}"

echo "================ FlashKV Build Script ================"

case "${TARGET,,}" in
  build)
    echo "Building .NET Solution..."
    dotnet build FlashKV.sln -c Release
    echo "Building Rust Benchmark Tool..."
    cargo build --release --manifest-path tools/flashkv-bench/Cargo.toml
    ;;
  test)
    echo "Running .NET Tests..."
    dotnet test FlashKV.sln -c Release --verbosity normal
    echo "Running Rust Tests..."
    cargo test --manifest-path tools/flashkv-bench/Cargo.toml
    ;;
  bench)
    echo "Running Micro-Benchmarks..."
    dotnet run --project benchmarks/FlashKV.Benchmarks/FlashKV.Benchmarks.csproj -c Release
    ;;
  all)
    echo "1. Building all projects..."
    dotnet build FlashKV.sln -c Release
    cargo build --release --manifest-path tools/flashkv-bench/Cargo.toml

    echo -e "\n2. Running test suite..."
    dotnet test FlashKV.sln -c Release --verbosity normal
    cargo test --manifest-path tools/flashkv-bench/Cargo.toml

    echo -e "\n3. Running micro-benchmarks..."
    dotnet run --project benchmarks/FlashKV.Benchmarks/FlashKV.Benchmarks.csproj -c Release

    echo -e "\n[SUCCESS] All FlashKV builds, tests, and benchmarks passed!"
    ;;
  *)
    echo "Unknown target: $TARGET. Use: build, test, bench, or all."
    exit 1
    ;;
esac
