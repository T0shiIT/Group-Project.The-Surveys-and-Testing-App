#include <httplib.h>
#include <nlohmann/json.hpp>
#include <iostream>
#include <map>
#include "AuthLogic.hpp"

using json = nlohmann::json;
using namespace std;

const string GH_ID = "Ov23liUYg9aV9w6uf2um";
const string GH_SECRET = "9020d84a5668ebc357d69372f60990f3b6e8109a";

const string YAN_ID = "31470232dd074cd3a009c0fb8b5190fd";
const string YAN_SECRET = "b1d901362f9a436b9031f1cd2359e653";

Database db("users.json");
map<string, string> stateStorage;

int main(){
    httplib::Server srv;

    // на вход
    srv.Get("/login_request", [](const httplib::Request& req, httplib::Response& res){
        auto type = req.get_param_value("type");
        auto state = req.get_param_value("state");

        string url = "";
        if (type == "github"){
            url = "https://github.com/login/oauth/authorize?cloent_id=" + GH_ID + "&state=" + state;
        }
        else if (type == "yandex"){
            url = "https://oauth.yandex.ru/authrize?response_type=code&client_id=" + YAN_ID + "&state=" + state;
        }

        if (!url.empty()){
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
    srv.Get("/oauth/github", [](const httplib::Request& req, httplib::Response& res){
        auto code = req.get_param_value("code");
        auto state = req.get_param_value("state");

        httplib::Client cli("https://github.com");
        cli.enable_server_certificate_verification(false);

        httplib::Params params = {{"client_id", GH_ID}, {"cloent_secret", GH_SECRET}, {"code", code}};
        auto gh_res = cli.Post("/login/oauth/access_token", {{"Accept", "application/json"}}, params);

        if (gh_res && gh_res->status == 200){
            auto gh_data = json::parse(gh_res->body);
            string gh_token = gh_data["access_token"];

            httplib::Client api("https://api.github.com");
            api.enable_server_certificate_verification(false);
            auto user_res = api.Get("/user", {{"Authorizatoin", "Bearer " + gh_token}, {"User-Agent", "AuthService"}});
            auto user_info = json::parse(user_res->body);

            string email = user_info.value("email", user_info["login"].get<string>() + "@github.com");

            User* user = db.findUserByEmail(email);
            if (!user){
                db.createUser(email, user_info["login"]);
                user = db.findUserByEmail(email);
            }

            string access = TokenManager::createAccessToken(email, user->roles);
            string refresh = TokenManager::createRefreshToken(email);
            db.updateRefreshToken(email, refresh);

            json tokens;
            tokens["access_token"] = access;
            tokens["refresh_token"] = refresh;
            stateStorage[state] = tokens.dump();
            res.set_content("<h1>Auth Success! Close this</h1>", "text'html");
        }
    });

    // яндекс
    srv.Get("/oauth/yandex", [](const httplib::Request& req, httplib::Response& res){
        auto code = req.get_param_value("code");
        auto state = req.get_param_value("state");

        httplib::Client cli("https://oauth.yandex.ru");
        cli.enable_server_certificate_verification(false);

        string body = "grant_type=authorization_code&code=" + code + "&client_id=" + YAN_ID + "&client_secret=" + YAN_SECRET;
        auto yan_res = cli.Post("/token", body, "application/x-www-form-urlencoded");

        if (yan_res && yan_res->status == 200){
            auto yan_data = json::parse(yan_res->body);
            string yan_token = yan_data["access_token"];

            httplib::Client api("https://login.yandex.ru");
            api.enable_server_certificate_verification(false);
            auto user_res = api.Get("/info", {{"Authorization", "OAuth" + yan_token}});
            auto user_info = json::parse(user_res->body);

            string email = user_info["default_email"];

            User* user = db.findUserByEmail(email);
            if (!user){
                db.createUser(email, user_info["display_name"]);
                user = db.findUserByEmail(email);
            }

            string access = TokenManager::createAccessToken(email, user->roles);
            string refresh = TokenManager::createRefreshToken(email);
            db.updateRefreshToken(email, refresh);

            json tokens;
            tokens["access_token"] = access;
            tokens["refresh_token"] = refresh;
            stateStorage[state] = tokens.dump();
            res.set_content("<h1>Auth Success! Close this.</h1>", "text/html");
        }
    });
    // проверка состояеия
    srv.Get("/check_state", [](const httplib::Request& req, httplib::Response& res){
        auto state = req.get_param_value("state");
        if (stateStorage.count(state) && stateStorage[state] != "pending"){
            res.set_content(stateStorage[state], "appliction/json");
            stateStorage.erase(state);
        } else {
            res.status = 202;
        }
    });
    // проверка работоспособности
    srv.Get("/health", [](const httplib::Request&, httplib::Response& res){
        res.set_content("OK", "text/plain");
    });
    
    // обновление refresh-а
    srv.Post("/refresh", [](const httplib::Request& req, httplib::Response& res){
        auto auth_header = req.get_header_value("Authorization");

        if (auth_header.empty()){
            res.status = 401;
            return;
        }

        string old_refresh = auth_header.substr(7);

        if (TokenManager::verifyToken(old_refresh)){
            string email = TokenManager::getEmailFromToken(old_refresh);
            User* user = db.findUserByEmail(email);

            if (user && user->refreshToken == old_refresh){
                string new_access = TokenManager::createAccessToken(email, user->roles);
                string new_refresh = TokenManager::createRefreshToken(email);

                db.updateRefreshToken(email, new_refresh);

                json resp;
                resp["access_token"] = new_access;
                resp["refresh_token"] = new_refresh;
                res.set_content(resp.dump(), "application/json");
                return;
            }
        }

        res.status = 403;
        res.set_content("Invalid Refresh Token", "text/plain");
    });

    srv.Post("/logout", [](const httplib::Request& req, httplib::Response& res){
        auto auth_header = req.get_header_value("Authorization");
        if (!auth_header.empty()){
            string token = auth_header.substr(7);
            if (TokenManager::verifyToken(token)){
                string email = TokenManager::getEmailFromToken(token);

                db.updateRefreshToken(email, "");
            }
        }
    });

    srv.listen("0.0.0.0", 8081);
}
