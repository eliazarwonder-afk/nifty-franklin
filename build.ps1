param(
    [string]$Target = "All"
)

$ErrorActionPreference = "Stop"

Write-Host "================ FlashKV Build Script ================" -ForegroundColor Cyan

switch ($Target.ToLowerInvariant()) {
    "build" {
        Write-Host "Building .NET Solution..." -ForegroundColor Yellow
        dotnet build FlashKV.sln -c Release
        Write-Host "Building Rust Benchmark Tool..." -ForegroundColor Yellow
        cargo build --release --manifest-path tools/flashkv-bench/Cargo.toml
    }
    "test" {
        Write-Host "Running .NET Tests..." -ForegroundColor Yellow
        dotnet test FlashKV.sln -c Release --verbosity normal
        Write-Host "Running Rust Tests..." -ForegroundColor Yellow
        cargo test --manifest-path tools/flashkv-bench/Cargo.toml
    }
    "bench" {
        Write-Host "Running Micro-Benchmarks..." -ForegroundColor Yellow
        dotnet run --project benchmarks/FlashKV.Benchmarks/FlashKV.Benchmarks.csproj -c Release
    }
    "all" {
        Write-Host "1. Building all projects..." -ForegroundColor Yellow
        dotnet build FlashKV.sln -c Release
        cargo build --release --manifest-path tools/flashkv-bench/Cargo.toml

        Write-Host "`n2. Running test suite..." -ForegroundColor Yellow
        dotnet test FlashKV.sln -c Release --verbosity normal
        cargo test --manifest-path tools/flashkv-bench/Cargo.toml

        Write-Host "`n3. Running micro-benchmarks..." -ForegroundColor Yellow
        dotnet run --project benchmarks/FlashKV.Benchmarks/FlashKV.Benchmarks.csproj -c Release

        Write-Host "`n[SUCCESS] All FlashKV builds, tests, and benchmarks passed!" -ForegroundColor Green
    }
    default {
        Write-Host "Unknown target: $Target. Use: Build, Test, Bench, or All." -ForegroundColor Red
        exit 1
    }
}
