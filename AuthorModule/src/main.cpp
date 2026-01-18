#include <httplib.h>
#include <nlohmann/json.hpp>
#include <iostream>
#include <map>
#include "AuthLogic.hpp"
using json = nlohmann::json;
using namespace std;

const string GH_ID = "Ov23lizovwQc16hqkwGz";
const string GH_SECRET = "225c16a3c30146564077d3d08d910812210558ef";
const string YAN_ID = "02a1500029c8496187c5ef080e4acdfd";
const string YAN_SECRET = "d7e57164251e46baada1155606f6cdb1";

Database db("users.json");
map<string, string> stateStorage;

int main() {
    httplib::Server srv;
    
    // Настройка сервера для принятия подключений
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
    
    // на вход
    srv.Get("/login_request", [](const httplib::Request& req, httplib::Response& res) {
        auto type = req.get_param_value("type");
        auto state = req.get_param_value("state");
        string url = "";
        if (type == "github") {
            url = "https://github.com/login/oauth/authorize?client_id=" + GH_ID +
                  "&redirect_uri=https://meaning-remain-creative-seriously.trycloudflare.com/auth/oauth/github" +
                  "&state=" + state;
        }
        else if (type == "yandex") {
            url = "https://oauth.yandex.ru/authorize?response_type=code&client_id=" + YAN_ID +
                  "&redirect_uri=https://meaning-remain-creative-seriously.trycloudflare.com/auth/oauth/yandex" +
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
    
    // github
    srv.Get("/oauth/github", [](const httplib::Request& req, httplib::Response& res) {
        auto code = req.get_param_value("code");
        auto state = req.get_param_value("state");
        if (code.empty() || state.empty()) {
            res.status = 400;
            res.set_content("Missing code or state", "text/plain");
            return;
        }
        httplib::Client cli("https://github.com");
        cli.enable_server_certificate_verification(false);
        httplib::Params params = {{"client_id", GH_ID}, {"client_secret", GH_SECRET}, {"code", code}};
        auto gh_res = cli.Post("/login/oauth/access_token", {{"Accept", "application/json"}}, params);
        
        if (gh_res && gh_res->status == 200) {
            try {
                auto gh_data = json::parse(gh_res->body);
                string gh_token = gh_data["access_token"].get<string>();
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
                
                // Получаем email
                if (user_info.contains("email") && !user_info["email"].is_null() && !user_info["email"].get<string>().empty()) {
                    email = user_info["email"].get<string>();
                } else {
                    email = user_info["login"].get<string>() + "@github.com";
                }
                
                // Получаем имя пользователя
                if (user_info.contains("name") && !user_info["name"].is_null() && !user_info["name"].get<string>().empty()) {
                    username = user_info["name"].get<string>();
                } else if (user_info.contains("login")) {
                    username = user_info["login"].get<string>();
                }
                
                User* user = db.findUserByEmail(email);
                if (!user) {
                    db.createUser(email, {"Student"});
                    user = db.findUserByEmail(email);
                }
                string access = TokenManager::createAccessToken(email, user->roles, username);
                string refresh = TokenManager::createRefreshToken(email, username);
                db.updateRefreshToken(email, refresh);
                
                json tokens;
                tokens["access_token"] = access;
                tokens["refresh_token"] = refresh;
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
    
    // яндекс
    srv.Get("/oauth/yandex", [](const httplib::Request& req, httplib::Response& res) {
        auto code = req.get_param_value("code");
        auto state = req.get_param_value("state");
        if (code.empty() || state.empty()) {
            res.status = 400;
            res.set_content("Missing code or state", "text/plain");
            return;
        }
        httplib::Client cli("https://oauth.yandex.ru");
        cli.enable_server_certificate_verification(false);
        string body = "grant_type=authorization_code&code=" + code + "&client_id=" + YAN_ID + "&client_secret=" + YAN_SECRET;
        auto yan_res = cli.Post("/token", body, "application/x-www-form-urlencoded");
        
        if (yan_res && yan_res->status == 200) {
            try {
                auto yan_data = json::parse(yan_res->body);
                string yan_token = yan_data["access_token"].get<string>();
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
                
                // Получаем email
                if (user_info.contains("default_email")) {
                    email = user_info["default_email"].get<string>();
                } else {
                    res.status = 500;
                    res.set_content("Invalid Yandex response", "text/plain");
                    return;
                }
                
                // Получаем имя пользователя
                if (user_info.contains("first_name") && user_info.contains("last_name")) {
                    username = user_info["first_name"].get<string>() + " " + user_info["last_name"].get<string>();
                } else if (user_info.contains("display_name")) {
                    username = user_info["display_name"].get<string>();
                } else if (user_info.contains("login")) {
                    username = user_info["login"].get<string>();
                }
                
                User* user = db.findUserByEmail(email);
                if (!user) {
                    db.createUser(email, {"Student"});
                    user = db.findUserByEmail(email);
                }
                string access = TokenManager::createAccessToken(email, user->roles, username);
                string refresh = TokenManager::createRefreshToken(email, username);
                db.updateRefreshToken(email, refresh);
                
                json tokens;
                tokens["access_token"] = access;
                tokens["refresh_token"] = refresh;
                stateStorage[state] = tokens.dump();

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
    
    // проверка состояния
    srv.Get("/check_state", [](const httplib::Request& req, httplib::Response& res) {
        auto state = req.get_param_value("state");
        if (state.empty()) {
            res.status = 400;
            res.set_content("Missing state parameter", "text/plain");
            return;
        }
        if (stateStorage.count(state) && stateStorage[state] != "pending") {
            res.set_content(stateStorage[state], "application/json");
            stateStorage.erase(state);
        } else {
            res.status = 202;
            res.set_content("Authorization in progress", "text/plain");
        }
    });
    
    // новый эндпоинт для валидации токена (для TestAppLogic)
    srv.Get("/validate_token", [](const httplib::Request& req, httplib::Response& res) {
        auto token = req.get_header_value("Authorization");
        if (token.empty() || token.find("Bearer ") != 0) {
            res.status = 401;
            res.set_content("Missing or invalid Authorization header", "text/plain");
            return;
        }
        token = token.substr(7); // Удаляем "Bearer "
        
        if (!TokenManager::verifyToken(token)) {
            res.status = 401;
            res.set_content("Invalid token", "text/plain");
            return;
        }
        
        try {
            auto decoded = jwt::decode<jwt::traits::nlohmann_json>(token);
            json response;
            response["user_id"] = decoded.get_payload_claim("user_id").as_string();
            response["username"] = decoded.get_payload_claim("username").as_string();
            response["email"] = decoded.get_payload_claim("email").as_string();
            response["permissions"] = decoded.get_payload_claim("permissions").as_array();
            response["valid"] = true;
            res.set_content(response.dump(), "application/json");
        } catch (const std::exception& e) {
            res.status = 401;
            res.set_content("Invalid token format", "text/plain");
        }
    });
    
    // проверка работоспособности
    srv.Get("/health", [](const httplib::Request&, httplib::Response& res) {
        res.set_content("OK", "text/plain");
    });
    
    cout << "Authorization service started on port 8081" << endl;
    srv.listen("0.0.0.0", 8081);
}