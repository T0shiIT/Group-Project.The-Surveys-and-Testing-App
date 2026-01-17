const express = require('express');
const app = express();
const port = 3000;
const path = require("path");
const redis = require('redis');
const cookieParser = require('cookie-parser');
const crypto = require("crypto");
const axios = require('axios');

//порты для авторизации!!!!!!!!!!!!!!!
const Main_modul_URL = 'http://localhost:8080'
const Auth_modul_URL = 'http://localhost:8081';

//подключение к redis
const redisClient = createClient({ url: 'redis://localhost:6379' });
redisClient.on('error', (err) => console.log('Redis Client Error', err));
redisClient.connect();


app.use(cookieParser());


app.use(async(req,res,next)=>{
    let sessionToken = req.cookies['session_id'];
    if (!sessionToken){
        console.log('неизвестный пользователь');          
        return handleUnknowUser(req,res);
    }


    const userData = await redisClient.get(sessionToken);
    if (!userData){
        console.log('неизвестный пользователь');          
        return handleUnknowUser(req,res);
    }


    const userData2 = JSON.parse(userData);
    console.log('статус пользователя', userData2.status);


    if (userData2.status === "Anonymous"){
        return handleAnonymousUser(req,res,sessionToken,userData2);
    }else if (userData2.status === "Authorized"){
        return handleAuthorizedUser(req,res,sessionToken,userData2);
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

        await redisClient.set(newSessionToken,dataToSave);
        try {
            const response = await axios.post(`${Auth_modul_URL}/auth-request`, {
                type: type,
                login_token: newLoginToken
            });

            res.cookie('session_id', newSessionToken, {httpOnly: true});

            if (response.data.redirectURL){
                return res.redirect(response.data.redirectURL);
            } else {
                return res.send('Ошибка: модуль авторизации не прислал ссылку');
            }

        } catch(e) {
            console.error('ошибка связи с модулем авторизации:', e.message);
            return res.status(500).send('ошибка авторизации');
        }
        
    }
    return res.redirect('/');
}

async function handleAnonymousUser(req,res,sessionToken,userData2) {
    if (req.path === '/login' && req.query.type) {
        const newLoginToken =crypto.randomUUID();
        userData2.loginToken = newLoginToken;

        await redisClient.set(sessionToken, JSON.stringify(userData2));
        
        try {
            const response = await axios.post(`${Auth_modul_URL}/auth-request`, {
                type: type,
                login_token: newLoginToken
            });

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
        return res.send("перезапуск входа");
    }

    try {
        const authResponse = await axios.post(`${Auth_modul_URL}/check-token`, {
            login_token: userData2.loginToken
        });

        if (authResponse.data.status === 'error' || authResponse.data.status === 'denied') {
            await redisClient.del(sessionToken);
            return res.redirect('/');
        }

        if (authResponse.data.status === 'success') {
            const { accessToken, refreshToken } = authResponse.data;
            const newUserData = {
                status: 'Authorized',
                accessToken: accessToken,
                refreshToken: refreshToken
            };

            await redisClient.set(sessionToken, JSON.stringify(newUserData));
            return res.redirect(req.originalURL);
        }

        return res.send("<h1>ожидайте подтверждения входа</h1><script>setTimeout(() => location.reload(), 2000);</script>");

    } catch (e) {
        console.log("модуль авторизации вернул ошибку");
        return res.redirect('/');
    }
}

async function handleAuthorizedUser(req,res, sessionToken, userData2) {
    if (req.path === '/') {
        return res.sendFile(path.join(__dirname, 'sait', 'index2.html'));
    }


    if (req.path.startsWith('/login')) {
        return res.redirect('/');
    }



    if (req.path === '/logout') {
        const logoutall = req.query.all === 'true';
        await redisClient.del(sessionToken);
        res.clearCookie('session_id');

        if (logoutall) {
            try {
                await axios.post(`${Auth_modul_URL}/logout`, { refreshToken: userData2.refreshToken});
            } catch (e) {console.error('Ошибка на сервере авторизации'); }
        }

        return res.redirect('/');
    }
    try {
        const mainModulURL = Main_module_URL;
        const response = await axios({
            method: req.method,
            url: mainModulURL,
            headers: { 
                'Authorization': 'Bearer ${userData2.accessToken}'
            },
            data: req.body
        });

        return res.send(response.data);
    } catch (error) {
        if (!error.response) {
            return res.status(500).send("ошибка главного модуля");
        }

        const status = error.response.status;
        if (status === 403) {
            return res.status(403).send("у вас нет прав");
        }

        if (status === 401) {
            try{
                const refreshResponse = await axios.post(`${Auth_modul_URL}/refresh`, {refreshToken: userData2.refreshToken});
                const {accessToken, refreshToken} = refreshResponse.data;

                await redisClient.set(
                    sessionToken,
                    JSON.stringify({
                        status: 'Authorized',
                        accessToken,
                        refreshToken
                    })
                );
                const retryResponse = await axios({
                    method: req.method,
                    url: mainModulURL,
                    headers: { 
                        'Authorization': 'Bearer ${accessToken}'
                    },
                    data: req.body
                });
                return res.send(retryResponse.data);
            }catch (error) {
                if (error.response && error.response.status === 401){
                    await redisClient.del(sessionToken);
                    res.clearCookie('session_id');
                    return res.redirect('/');
                }
                return res.status(500).send("ошибка обновления токена");
            }
        }
        return res.status(status).send(error.response.data);
    }
}

app.listen(port,()=>{
    console.log("web-client запущен")
});