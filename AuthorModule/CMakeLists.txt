cmake_minimum_required(VERSION 3.14)
project(AuthService)

set(CMAKE_CXX_STANDARD 17)


# nlohmann/json
include(FetchContent)
FetchContent_Declare(json URL https://github.com/nlohmann/json/releases/download/v3.11.2/json.tar.xz)
FetchContent_MakeAvailable(json)

# cpp-httplib
FetchContent_Declare(httplib URL https://github.com/yhirose/cpp-httplib/archive/refs/tags/v0.14.3.tar.gz)
FetchContent_MakeAvailable(httplib)

# jwt-cpp
FetchContent_Declare(jwt-cpp URL https://github.com/Thalhammer/jwt-cpp/archive/refs/tags/v0.7.0.tar.gz)
FetchContent_MakeAvailable(jwt-cpp)

add_executable(auth_service src/main.cpp)
