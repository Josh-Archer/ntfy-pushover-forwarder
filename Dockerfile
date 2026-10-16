FROM rust:1.98-slim-bookworm AS build
WORKDIR /build
COPY Cargo.toml Cargo.lock ./
COPY src/ ./src/
RUN cargo build --release --locked

FROM debian:bookworm-slim AS final
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /app /data && chown 1654:1654 /app /data
WORKDIR /app
COPY --from=build /build/target/release/ntfy-pushover-forwarder /usr/local/bin/ntfy-pushover-forwarder
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["/usr/local/bin/ntfy-pushover-forwarder"]
