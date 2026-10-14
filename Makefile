.PHONY: all build test bench clean docker run run-bench

all: build test

build:
	dotnet build FlashKV.sln -c Release
	cargo build --release --manifest-path tools/flashkv-bench/Cargo.toml

test:
	dotnet test FlashKV.sln -c Release --verbosity normal
	cargo test --manifest-path tools/flashkv-bench/Cargo.toml

bench:
	dotnet run --project benchmarks/FlashKV.Benchmarks/FlashKV.Benchmarks.csproj -c Release

run:
	dotnet run --project src/FlashKV.Server/FlashKV.Server.csproj

run-bench:
	cargo run --release --manifest-path tools/flashkv-bench/Cargo.toml -- -p 6379 -n 100000 -c 20

docker:
	docker build -t flashkv:latest .

clean:
	dotnet clean FlashKV.sln
	cargo clean --manifest-path tools/flashkv-bench/Cargo.toml
