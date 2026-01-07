const express = require('express');
const app = express();
const port = 3000;
const pathc = require("path");
const redis = require('redis');
const cookieParser = require('cooki-parser');

//подключение к redis
const redisClient = redis.createClient();
(async () => {
    await redisclient.connect();
})();
console.log("Connecting to the Redis");
redisclient.on("ready", () => {
    console.log("Connected!");
});
redisclient.on("error", (err) => {
    console.log("Error in the Connection",err);
});


app.use(cookieParser(''));


app.use(async(req,res,next)=>{
    let sessionToken = req.cookies['session_id'];
    if (!sessionToken){
        console.log('неизвестный пользователь');          
        return handleUnknowUser(req,res);
    }


    const userData = await 
    redisClient.get(sessionToken);



    if (!userData){
        console.log('неизвестный пользователь');          
        return handleUnknowUser(req,res);
    }


    const userData2 = JSON.parse(userData);
    console.log('статус пользователя', userData);

    if (userData === "Anonymous"){
        return handleAnonymousUser(req,res,sessionToken,userData);
    }else if (userData === "Authorized"){
        return handleAuthorizedUser(req,res,sessionToken,userData);
    }else{
        return handleUnknowUser(req,res);
    }
});

app.listen(port,()=>{
    console.log("web-client запущен")
});