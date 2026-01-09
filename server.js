const express = require('express');
const app = express();
const port = 3000;
const pathc = require("path");
const redis = require('redis');
const cookieParser = require('cooki-parser');
const crypto = require("crypto");
const axios = require('axios');

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


async function handleUnknowUser(req,res) {
    if (req.path === '/'){
        return res.sendFile(path.join(__dirname,'sait','index.html'));
    }


    if (req.path === '/login' && req.query.type){
        const type = req.query.type

        const newSessionToken = crypto.randomUUID();
        const newLoginToken = crypto.randomUUID();

        const dataToSave = JSON.stringify({
            status: 'Anonymous',
            loginToken: newLoginToken
        });

        await redisClient.set(newLoginToken,dataToSave);
        try {
            const response = await axios.post('???') {
                type: type,
                login_token: newLoginToken
            };

            res.cookie('session_id', newSessionToken, {httpOnly: true});

            if (response.data.redirectURL){
                return res.redirect(response.data.redirectURL);
            } else {
                return res.send('Ошибка: модуль авторизации не прислал ссылку');
            }

        } catch(e) {
            console.error('ошибам связи с модулем авторизации:', e.massage);
            return res.status(500).send('ошибка авторизации');
        }
        return res.redirect('/');
    }
}






app.listen(port,()=>{
    console.log("web-client запущен")
});