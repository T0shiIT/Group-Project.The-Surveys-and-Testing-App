#include <httplib.h>
#include <nlohmann/json.hpp>
#include <iostream>
#include <map>
#include <unordered_set>
#include <cstdlib>
#include <string>
#include "AuthLogic.hpp"
using json = nlohmann::json;
using namespace std;

// Конфигурация приложения
const string GH_ID = "Ov23lizovwQc16hqkwGz";
const string GH_SECRET = "225c16a3c30146564077d3d08d910812210558ef";
const string YAN_ID = "02a1500029c8496187c5ef080e4acdfd";
const string YAN_SECRET = "d7e57164251e46baada1155606f6cdb1";
const string JWT_SECRET = "super_secret_key_123";
const string REFRESH_SECRET = "refresh_secret_key_456";

// Глобальные переменные
Database db("users.json");
map<string, string> stateStorage; // login_token -> status/tokens
unordered_set<string> revokedRefreshTokens; // Отмененные refresh токены

// Получаем URL из переменных окружения
string GetCloudflareUrl() {
    const char* url = getenv("CLOUDFLARE_URL");
    if (url && strlen(url) > 0) {
        return url;
    }
    return "https://providing-tee-bath-evolution.trycloudflare.com";
}

// Проверка refresh токена
bool verifyRefreshToken(const string& token) {
    try {
        auto verifier = jwt::verify<jwt::traits::nlohmann_json>()
            .allow_algorithm(jwt::algorithm::hs256{REFRESH_SECRET})
            .with_issuer("auth_service");
        auto decoded = jwt::decode<jwt::traits::nlohmann_json>(token);
        verifier.verify(decoded);
        return true;
    } catch (...) {
        return false;
    }
}

// Получение email из refresh токена
string getEmailFromRefreshToken(const string& token) {
    auto decoded = jwt::decode<jwt::traits::nlohmann_json>(token);
    return decoded.get_payload_claim("email").as_string();
}

int main() {
    httplib::Server srv;
    string cloudflareUrl = GetCloudflareUrl();
    
    // Настройка CORS
    srv.set_default_headers({
        {"Access-Control-Allow-Origin", "*"},
        {"Access-Control-Allow-Methods", "GET, POST, OPTIONS"},
        {"Access-Control-Allow-Headers", "Content-Type"}
    });
    
    // Обработчик OPTIONS для CORS
    srv.Options(".*", [](const httplib::Request& req, httplib::Response& res) {
        res.set_header("Access-Control-Allow-Origin", "*");
        res.set_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.set_header("Access-Control-Allow-Headers", "Content-Type");
    });
    
    //---[НАЧАЛО ПРОЦЕССА АВТОРИЗАЦИИ]---
    srv.Get("/login_request", [&cloudflareUrl](const httplib::Request& req, httplib::Response& res) {
        auto type = req.get_param_value("type");
        auto state = req.get_param_value("state"); // login_token
        
        string url = "";
        if (type == "github") {
            url = "https://github.com/login/oauth/authorize?client_id=" + GH_ID +
                  "&redirect_uri=" + cloudflareUrl + "/auth/oauth/github" +
                  "&state=" + state;
        }
        else if (type == "yandex") {
            url = "https://oauth.yandex.ru/authorize?response_type=code&client_id=" + YAN_ID +
                  "&redirect_uri=" + cloudflareUrl + "/auth/oauth/yandex" +
                  "&state=" + state;
        }
        
        if (!url.empty()) {
            stateStorage[state] = "pending";
            json resp;
            resp["url"] = url;
            res.set_content(resp.dump(), "application/json");
        } else {
            res.status = 400;
            res.set_content("Unknown provider type", "text/plain");
        }
    });
    
    //---[ОБРАТНЫЙ ВЫЗОВ ДЛЯ GITHUB]---
    srv.Get("/oauth/github", [&cloudflareUrl](const httplib::Request& req, httplib::Response& res) {
        auto code = req.get_param_value("code");
        auto state = req.get_param_value("state"); // login_token
        
        if (code.empty() || state.empty()) {
            res.status = 400;
            res.set_content("Missing code or state", "text/plain");
            return;
        }
        
        // Получаем access token от GitHub
        httplib::Client cli("https://github.com");
        cli.enable_server_certificate_verification(false);
        httplib::Params params = {{"client_id", GH_ID}, {"client_secret", GH_SECRET}, {"code", code}};
        auto gh_res = cli.Post("/login/oauth/access_token", {{"Accept", "application/json"}}, params);
        
        if (gh_res && gh_res->status == 200) {
            try {
                auto gh_data = json::parse(gh_res->body);
                string gh_token = gh_data["access_token"].get<string>();
                
                // Получаем информацию о пользователе
                httplib::Client api("https://api.github.com");
                api.enable_server_certificate_verification(false);
                auto user_res = api.Get("/user", {{"Authorization", "Bearer " + gh_token}, {"User-Agent", "AuthService"}});
                
                if (!user_res || user_res->status != 200) {
                    res.status = 500;
                    res.set_content("Failed to get user info", "text/plain");
                    return;
                }
                
                auto user_info = json::parse(user_res->body);
                string email;
                string username = "Пользователь";
                
                // Получаем email и имя пользователя
                if (user_info.contains("email") && !user_info["email"].is_null() && !user_info["email"].get<string>().empty()) {
                    email = user_info["email"].get<string>();
                } else {
                    email = user_info["login"].get<string>() + "@github.com";
                }
                
                if (user_info.contains("name") && !user_info["name"].is_null() && !user_info["name"].get<string>().empty()) {
                    username = user_info["name"].get<string>();
                } else if (user_info.contains("login")) {
                    username = user_info["login"].get<string>();
                }
                
                // Находим или создаем пользователя
                User* user = db.findUserByEmail(email);
                if (!user) {
                    db.createUser(email, {"Student"});
                    user = db.findUserByEmail(email);
                }
                
                // Создаем токены
                string access = TokenManager::createAccessToken(email, user->roles, username);
                string refresh = TokenManager::createRefreshToken(email, username);
                db.updateRefreshToken(email, refresh);
                
                // Формируем ответ
                json tokens;
                tokens["access_token"] = access;
                tokens["refresh_token"] = refresh;
                tokens["email"] = email;
                tokens["username"] = username;
                
                // Сохраняем токены для login_token
                stateStorage[state] = tokens.dump();
                
                res.set_content(
                    "<!DOCTYPE html>"
                    "<html><head><meta charset=\"UTF-8\"><script>window.close();</script></head>"
                    "<body><h1>✅ Авторизация успешна! Закройте это окно</h1></body></html>",
                    "text/html; charset=UTF-8"
                );
            }
            catch (const std::exception& e) {
                cerr << "Error processing GitHub response: " << e.what() << endl;
                res.status = 500;
                res.set_content("Internal server error", "text/plain");
            }
        } else {
            res.status = 500;
            res.set_content("GitHub authentication failed", "text/plain");
        }
    });
    
    //---[ОБРАТНЫЙ ВЫЗОВ ДЛЯ YANDEX]---
    srv.Get("/oauth/yandex", [&cloudflareUrl](const httplib::Request& req, httplib::Response& res) {
        auto code = req.get_param_value("code");
        auto state = req.get_param_value("state"); // login_token
        
        if (code.empty() || state.empty()) {
            res.status = 400;
            res.set_content("Missing code or state", "text/plain");
            return;
        }
        
        // Получаем access token от Yandex
        httplib::Client cli("https://oauth.yandex.ru");
        cli.enable_server_certificate_verification(false);
        string body = "grant_type=authorization_code&code=" + code + "&client_id=" + YAN_ID + "&client_secret=" + YAN_SECRET;
        auto yan_res = cli.Post("/token", body, "application/x-www-form-urlencoded");
        
        if (yan_res && yan_res->status == 200) {
            try {
                auto yan_data = json::parse(yan_res->body);
                string yan_token = yan_data["access_token"].get<string>();
                
                // Получаем информацию о пользователе
                httplib::Client api("https://login.yandex.ru");
                api.enable_server_certificate_verification(false);
                auto user_res = api.Get("/info", {{"Authorization", "OAuth " + yan_token}});
                
                if (!user_res || user_res->status != 200) {
                    res.status = 500;
                    res.set_content("Failed to get user info", "text/plain");
                    return;
                }
                
                auto user_info = json::parse(user_res->body);
                string email;
                string username = "Пользователь";
                
                // Получаем email и имя пользователя
                if (user_info.contains("default_email")) {
                    email = user_info["default_email"].get<string>();
                } else {
                    res.status = 500;
                    res.set_content("Invalid Yandex response", "text/plain");
                    return;
                }
                
                if (user_info.contains("first_name") && user_info.contains("last_name")) {
                    username = user_info["first_name"].get<string>() + " " + user_info["last_name"].get<string>();
                } else if (user_info.contains("display_name")) {
                    username = user_info["display_name"].get<string>();
                } else if (user_info.contains("login")) {
                    username = user_info["login"].get<string>();
                }
                
                // Находим или создаем пользователя
                User* user = db.findUserByEmail(email);
                if (!user) {
                    db.createUser(email, {"Student"});
                    user = db.findUserByEmail(email);
                }
                
                // Создаем токены
                string access = TokenManager::createAccessToken(email, user->roles, username);
                string refresh = TokenManager::createRefreshToken(email, username);
                db.updateRefreshToken(email, refresh);
                
                // Формируем ответ
                json tokens;
                tokens["access_token"] = access;
                tokens["refresh_token"] = refresh;
                tokens["email"] = email;
                tokens["username"] = username;
                
                // Сохраняем токены для login_token
                stateStorage[state] = tokens.dump();
                
                res.set_content(
                    "<!DOCTYPE html>"
                    "<html><head><meta charset=\"UTF-8\"><script>window.close();</script></head>"
                    "<body><h1>✅ Авторизация успешна! Закройте это окно</h1></body></html>",
                    "text/html; charset=UTF-8"
                );
            }
            catch (const std::exception& e) {
                cerr << "Error processing Yandex response: " << e.what() << endl;
                res.status = 500;
                res.set_content("Internal server error", "text/plain");
            }
        } else {
            res.status = 500;
            res.set_content("Yandex authentication failed", "text/plain");
        }
    });
    
    //---[ПРОВЕРКА СТАТУСА АВТОРИЗАЦИИ ПО ТОКЕНУ ВХОДА]---
    srv.Get("/check_state", [](const httplib::Request& req, httplib::Response& res) {
        auto state = req.get_param_value("state"); // login_token
        
        if (state.empty()) {
            res.status = 400;
            res.set_content("Missing state parameter", "text/plain");
            return;
        }
        
        if (stateStorage.count(state)) {
            if (stateStorage[state] == "pending") {
                // Авторизация еще в процессе
                res.status = 202; // Accepted
                res.set_content("Authorization in progress", "text/plain");
            } else {
                // Авторизация завершена - возвращаем токены
                res.set_content(stateStorage[state], "application/json");
                stateStorage.erase(state); // Удаляем после использования
            }
        } else {
            // Токен не найден или истек
            res.status = 404;
            res.set_content("Token not found or expired", "text/plain");
        }
    });
    
    //---[ОБНОВЛЕНИЕ ACCESS ТОКЕНА]---
    srv.Post("/refresh", [](const httplib::Request& req, httplib::Response& res) {
        if (!req.has_header("Content-Type") || req.get_header_value("Content-Type") != "application/json") {
            res.status = 400;
            res.set_content("Invalid Content-Type", "text/plain");
            return;
        }
        
        try {
            auto data = json::parse(req.body);
            string refreshToken = data["refresh_token"].get<string>();
            
            // Проверяем, не отменен ли refresh токен
            if (revokedRefreshTokens.find(refreshToken) != revokedRefreshTokens.end()) {
                res.status = 401;
                res.set_content("Refresh token has been revoked", "text/plain");
                return;
            }
            
            // Проверяем валидность refresh токена
            if (!verifyRefreshToken(refreshToken)) {
                res.status = 401;
                res.set_content("Invalid refresh token", "text/plain");
                return;
            }
            
            // Получаем email из токена
            string email = getEmailFromRefreshToken(refreshToken);
            
            // Находим пользователя
            User* user = db.findUserByEmail(email);
            if (!user) {
                res.status = 404;
                res.set_content("User not found", "text/plain");
                return;
            }
            
            string username = "Пользователь";
            // Пытаемся получить имя пользователя из базы (если оно там есть)
            // В простом варианте используем email как имя
            if (email.find('@') != string::npos) {
                username = email.substr(0, email.find('@'));
            }
            
            // Создаем новые токены
            string newAccess = TokenManager::createAccessToken(email, user->roles, username);
            string newRefresh = TokenManager::createRefreshToken(email, username);
            
            // Обновляем refresh токен в базе
            db.updateRefreshToken(email, newRefresh);
            
            // Добавляем старый токен в черный список
            revokedRefreshTokens.insert(refreshToken);
            
            // Формируем ответ
            json tokens;
            tokens["access_token"] = newAccess;
            tokens["refresh_token"] = newRefresh;
            res.set_content(tokens.dump(), "application/json");
        } catch (const exception& e) {
            res.status = 400;
            res.set_content("Invalid request format: " + string(e.what()), "text/plain");
        }
    });
    
    //---[ВЫХОД ИЗ СИСТЕМЫ]---
    srv.Post("/logout", [](const httplib::Request& req, httplib::Response& res) {
        if (!req.has_header("Content-Type") || req.get_header_value("Content-Type") != "application/json") {
            res.status = 400;
            res.set_content("Invalid Content-Type", "text/plain");
            return;
        }
        
        try {
            auto data = json::parse(req.body);
            string refreshToken = data["refresh_token"].get<string>();
            
            // Добавляем токен в черный список
            revokedRefreshTokens.insert(refreshToken);
            
            // Получаем email из токена
            string email = getEmailFromRefreshToken(refreshToken);
            
            // Очищаем refresh токен в базе
            db.updateRefreshToken(email, "");
            
            json response;
            response["message"] = "Successfully logged out on all devices";
            res.set_content(response.dump(), "application/json");
        } catch (const exception& e) {
            res.status = 400;
            res.set_content("Invalid request format: " + string(e.what()), "text/plain");
        }
    });
    
    //---[ПРОВЕРКА РАБОТОСПОСОБНОСТИ]---
    srv.Get("/health", [](const httplib::Request&, httplib::Response& res) {
        res.set_content("OK", "text/plain");
    });
    
    cout << "Authorization service started on port 8081" << endl;
    cout << "Cloudflare URL: " << cloudflareUrl << endl;
    srv.listen("0.0.0.0", 8081);
}