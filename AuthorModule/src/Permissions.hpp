#pragma once
#include <vector>
#include <string>
#include <map>

using std::string;
using std::vector;
using std::map;

inline vector<string> getPermissionsByRole(const string& role){
    static const map<string, vector<string>> roles ={
        {"Student", {}},
        {"Teacher", {
            "user:data:read",
            "course:testList", "course:test:read", "course:userList", "course:user:add", 
            "course:user:del", "course:add",
            "quest:list:read", "quest:read", "quest:create"
        }},
        {"Admin", {
            "user:list:read", "user:fullName:write", "user:data:read", "user:roles:read",
            "user:roles:write", "user:block:read", "user:block:write",
            "course:info:write", "course:testList", "course:test:read", "course:test:write",
            "course:test:add", "course:test:del", "course:userList", "course:user:add", 
            "course:user:del", "course:add", "course:del", 
            "quest:list:read", "quest:read", "quest:update", "quest:create", "quest:del",
            "test:quest:del", "test:quest:add", "test:quest:update","test:answer:read", 
            "answer:read", "answer:update", "answer:del"
        }}
    };

    if (roles.count(role)){
        return roles.at(role);
    }
    return {};
}
