#pragma once
#include <iostream>
#include <fstream>
#include <string>
#include <nlohmann/json.hpp>

using std::string;
using std::vector;
using json = nlohmann::json;

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
};
