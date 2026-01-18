#pragma once
#include <iostream>
#include <vector>
#include <string>
#include <fstream>
#include <set>
#include <chrono>
#include <nlohmann/json.hpp>
#include <jwt-cpp/jwt.h>
#include <jwt-cpp/traits/nlohmann-json/traits.h>
#include "Permissions.hpp"
using namespace std;
using json = nlohmann::json;
using nlohmann_claim = jwt::basic_claim<jwt::traits::nlohmann_json>;

struct User {
    string email;
    vector<string> roles;
    string refreshToken;
};

class Database {
private:
    string filename;
    json dbData;
public:
    Database(string f) : filename(f) {
        ifstream ifile(filename);
        if (ifile) {
            ifile >> dbData;
        } else {
            dbData = json::array();
        }
    }
    
    void save() {
        ofstream f(filename);
        f << dbData.dump(4);
    }
    
    User* findUserByEmail(const string& email) {
        for (auto& element : dbData) {
            if (element["email"] == email) {
                static User u;
                u.email = element["email"];
                u.roles = element["roles"].get<vector<string>>();
                u.refreshToken = element["refreshToken"];
                return &u;
            }
        }
        return nullptr;
    }
    
    void createUser(const string& email, const vector<string>& roles) {
        if (!findUserByEmail(email)) {
            dbData.push_back({
                {"email", email},
                {"roles", roles},
                {"refreshToken", ""}
            });
            save();
        }
    }
    
    void updateRefreshToken(const string& email, const string& token) {
        for (auto& element : dbData) {
            if (element["email"] == email) {
                element["refreshToken"] = token;
                save();
                return;
            }
        }
    }
};

class TokenManager {
public:
    static string createAccessToken(const string& email, const vector<string>& roles, const string& username) {
        auto now = chrono::system_clock::now();
        const string JWT_SECRET = "super_secret_key_123";
        set<string> allPermissions;
        for (const auto& role : roles) {
            auto perms = getPermissionsByRole(role);
            for (const auto& p : perms) allPermissions.insert(p);
        }
        auto token = jwt::create<jwt::traits::nlohmann_json>()
            .set_issuer("auth_service")
            .set_type("JWS")
            .set_payload_claim("user_id", nlohmann_claim(email))      // Для TestAppLogic
            .set_payload_claim("email", nlohmann_claim(email))         // Для обратной совместимости
            .set_payload_claim("username", nlohmann_claim(username))  // Для отображения имени
            .set_payload_claim("permissions", nlohmann_claim(allPermissions))
            .set_issued_at(now)
            .set_expires_at(now + chrono::minutes(15))
            .sign(jwt::algorithm::hs256{JWT_SECRET});
        return token;
    }
    
    static string createRefreshToken(const string& email, const string& username) {
        auto now = chrono::system_clock::now();
        return jwt::create<jwt::traits::nlohmann_json>()
            .set_issuer("auth_service")
            .set_payload_claim("user_id", nlohmann_claim(email))      // Для TestAppLogic
            .set_payload_claim("email", nlohmann_claim(email))         // Для обратной совместимости
            .set_payload_claim("username", nlohmann_claim(username))  // Для отображения имени
            .set_issued_at(now)
            .set_expires_at(now + chrono::hours(24 * 7))
            .sign(jwt::algorithm::hs256{"refresh_secret_key_456"});
    }
    
    static bool verifyToken(const string& token) {
        try {
            auto verifier = jwt::verify<jwt::traits::nlohmann_json>()
                .allow_algorithm(jwt::algorithm::hs256{"super_secret_key_123"})
                .with_issuer("auth_service");
            auto decoded = jwt::decode<jwt::traits::nlohmann_json>(token);
            verifier.verify(decoded);
            return true;
        } catch (...) {
            return false;
        }
    }
    
    static string getUserIDFromToken(const string& token) {
        auto decoded = jwt::decode<jwt::traits::nlohmann_json>(token);
        return decoded.get_payload_claim("user_id").as_string();
    }
};