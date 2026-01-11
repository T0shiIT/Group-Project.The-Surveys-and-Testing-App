#include <httplib.h>

int main(){
    httplib::Server srv;
    srv.Get("/health", [](const httplib::Request&, httplib::Response& res){
        res.set_content("OK", "text/plain");
    });
    srv.listen("0.0.0.0", 8081);
}