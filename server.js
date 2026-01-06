const express = require('express');
const app = express();
const port = 3000;
const pathc = require("path");
const redis = require('redis');


/*
Неэффективный код, поменяю на app.use, чтобы программа была эфективней
app.get('/',(req,res)=>{
    res.sendFile(path.join(project,'index.html'))
});
*/

const redisClient = redis.createClient();
(async () => {
    await redisclient.connect();
})();
console.log("Connecting to the Redis");
redisclient.on("ready", () => {
    console.log("Connected!");
});
redisclient.on("error", (err) => {
    console.log("Error in the Connection");
});


app.listen(port,()=>{
    console.log("web-client запущен")
});