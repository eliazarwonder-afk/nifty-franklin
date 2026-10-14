# Contributing to FlashKV

Thank you for your interest in contributing to FlashKV! We welcome contributions from everyone—whether it's fixing bugs, adding new Redis commands, improving documentation, or optimizing low-level allocations.

---

## 🛠️ Development Setup

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- [Rust & Cargo 1.80+](https://rustup.rs/)

### Clone and Verify Build

```bash
git clone https://github.com/eliazarwonder-afk/flashkv.git
cd flashkv

# Verify .NET solution builds cleanly
dotnet build

# Verify all unit tests pass
dotnet test

# Verify Rust benchmarking tool builds cleanly
cargo check --manifest-path tools/flashkv-bench/Cargo.toml
```

---

## 🌿 Git Workflow & Branches

1. **Fork the repository** on GitHub.
2. **Create a topic branch** from `main`:
   ```bash
   git checkout -b feat/add-zset-commands
   # or
   git checkout -b fix/ttl-edge-case
   ```
3. **Commit with descriptive messages** following Conventional Commits:
   - `feat: implement SINTER and SUNION commands`
   - `fix: prevent potential race condition in active TTL sweep`
   - `perf: eliminate buffer copy in RespWriter.WritePrefixAndInteger`
   - `docs: update command matrix in README`
4. **Push your branch** to your fork:
   ```bash
   git push origin feat/add-zset-commands
   ```
5. **Open a Pull Request** against `main`.

---

## 📐 Coding Standards

### C# Guidelines (.NET 8)
- Enable **nullable reference types** (`<Nullable>enable</Nullable>`).
- Use **file-scoped namespaces** (`namespace FlashKV.Core;`).
- Target **zero or minimal allocations** on the hot path:
  - Prefer `ReadOnlySpan<byte>` and `ReadOnlyMemory<byte>` over string allocations when handling byte payloads.
  - Utilize `ArrayPool<byte>.Shared` or `System.IO.Pipelines` buffers where appropriate.
- Maintain existing test coverage for all newly added commands or edge cases.

### Rust Guidelines (2021 Edition)
- Ensure all Rust code is formatted via `cargo fmt`.
- Ensure clean clippy analysis without warnings:
  ```bash
  cargo clippy --manifest-path tools/flashkv-bench/Cargo.toml
  ```

---

## 🧪 Testing Requirements

Every bug fix or new command should include automated xUnit tests in `tests/FlashKV.Core.Tests`.

Run tests locally:
```bash
dotnet test --logger "console;verbosity=detailed"
```

---

## 💬 Community & Code of Conduct

We are committed to providing a welcoming, diverse, and harassment-free environment for all contributors. Please treat everyone with respect, kindness, and professionalism.
