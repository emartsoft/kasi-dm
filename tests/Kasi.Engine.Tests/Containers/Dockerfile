# One image, three roles (M1.16b): real vsftpd, Squid and Dante from the Ubuntu 24.04 packages, so the engine is tested against the
# software users actually run. The base is pinned by digest; each test starts the role it needs with its own configuration.
FROM ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends vsftpd squid dante-server openssl ca-certificates \
 && rm -rf /var/lib/apt/lists/*
