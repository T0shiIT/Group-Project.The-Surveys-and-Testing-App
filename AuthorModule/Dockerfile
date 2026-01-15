FROM ubuntu:24.04
RUN apt-get update && apt-get install -y \
    build-essential \
    cmake \
    git \
    libssl-dev \
    libcurl4-openssl-dev \
    ca-certificates \
    curl
WORKDIR /app
COPY . .
# Устанавливаем зависимости и собираем
RUN rm -rf build && mkdir build && cd build && cmake .. && make
CMD ["./build/auth_service"]