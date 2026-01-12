#pragma once
#include <iostream>
#include <fstream>
#include <string>
#include <nlohmann/json.hpp>
#include <jwt-cpp/jwt.h>
#include "Permissions.hpp"

using std::string;
using std::vector;
using json = nlohmann::json;

const string JWT_SECRET = "super_secret_group_project_key";

struct User {
    string email;
    string name;
    vector<string> roles;
    string refreshToken;
};

class Database {
    string filename ="user_db.json";
    json dbData;

public:
    Database() {
        std::ifstream f(filename);
        if(f.good()){
            f >> dbData;
        } else {
            dbData = json::array(); 
        }
    }

    void save(){
        std::ofstream f(filename);
        f << dbData.dump(4)
    }

    User* findUserByEmail (const string& email){
        for (auto& element : dbData){
            if (element["email"] == email){
                static User u;
                u.email = element["email"];
                u.name = element["name"];
                u.roles = element["roles"].get<vector<string>>();
                if (element.contains("refreshToken")) {
                    u.refreshToken = element["refreshToken"];
                }
                return &u;
            }
        }
        return nullptr;
    }

    void createUser(const string& email, const string& name){
        if (findUserByEmail(email)) return;

        json newUser;
        newUser["email"] = email;
        newUser["name"] = name;
        newUser["roles"] = {"Student"};
        newUser["refreshToken"] = "";

        dbData.push_back(newUser);
        save();
    }

    void updateRefreshToken(const string& email, const string& token){
        for (auto& element : dbData){
            if (element["email"] == email){
                element["refreshToken"] = token;
                save();
                retutn;
            }
        }
    }
};

class TokenManager{
public:
    static string createAccessToken(const string& email, const vector<string>& roles){
        auto now = std::chrono::system_clock::now();

        vector<string> allPermissions;
        for (const auto& role : roles){
            auto perms = getPermissionsByRole(role);
            allPermissions.insert(allPermissions.end(), perms.begin(), perms.end());
        }

        auto token jwt::create()
            .set_issuer("auth_service")
            .set_type("JWS")
            .set_payload_claim("email", jwt::claim(email))
            .set_payload_claim("permissions", jwt::claim(allPermissions))
            .set_issued_at(now)
            .set_expires_at(now + std::chrono::minutes(1))
            .sign(jwt::algorithm::hs256(JWT_SECRET));
        return token;
    }

    static string createRefreshToken(const string& email){
        auto now = std::chrono::system_clock::now();
        return jwt::create()
            .set_issuer("auth_service")
            .set_payload_claim("email", jwt::claim(email))
            .set_payload_claim("type", jwt::claim(string("refresh")))
            .set_issued_at(now)
            .set_expires_at(now + std::chrono::hours(24 * 7))
            .sign(jwt::algorithm::hs256{JWT_SECRET});
    }

    static bool verifyToken(const string& token){
        try {
            auto decoded = jwt::decode(token);
            auto verifer = jwt::verify()
                .allow_algorithm(jwt::algorithm::hs256{JWT_SECRET})
                .with_issuer("auth_service");
            verifer.verify(decoded)
            return true;
        } catch (const std::exception& e) {
            std::cout << "Auth Error: " << e.what() << std::endl;
            return false;
        }
    }

    static string getEmailFromToken(const string& token){
        auto decoded = jwt::decode(token);
        return decoded.get_payload_claim("email").as_string();
    }
};
