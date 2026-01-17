const express = require('express');
const app = express();
const port = 3000;
const path = require("path");
const { createClient } = require('redis');
const cookieParser = require('cookie-parser');
const crypto = require("crypto");
const axios = require('axios');

//порты для авторизации и главного модуля!!!!!!!!!!!!!!!
const Main_modul_URL = 'http://localhost:8080'
const Auth_modul_URL = 'http://localhost:8081';

//подключение к redis
const redisClient = createClient({ url: 'redis://localhost:6379' });
redisClient.on('error', (err) => console.log('Redis Client Error', err));
redisClient.connect();


app.use(cookieParser());


app.use(async(req,res,next)=>{ // 1.2 браузер автоматически оправляет куки
    let sessionToken = req.cookies['session_id'];

    //1.4 если куки нет-считаем,что ответ redis отрицательный
    if (!sessionToken){
        console.log('неизвестный пользователь');          
        return handleUnknowUser(req,res);
    }


    const userData = await redisClient.get(sessionToken); //1.4 Web Client делает запрос к компоненту Redis используя токен сессии из кук в качестве ключа
    
    //1.5 Redis сообщает, что такого ключа нет(неизвестный пользователь)
    if (!userData){
        console.log('неизвестный пользователь');          
        return handleUnknowUser(req,res);
    }


    const userData2 = JSON.parse(userData);
    console.log('статус пользователя', userData2.status);

    //2.6 Web Client достаёт из ответа статус пользователя. Он равен: Анонимный;
    if (userData2.status === "Anonymous"){
        return handleAnonymousUser(req,res,sessionToken,userData2);

    //3.6 Web Client достаёт из ответа статус пользователя. Он равен: Авторизованный;
    }else if (userData2.status === "Authorized"){
        return handleAuthorizedUser(req,res,sessionToken,userData2);
    }else{
        return handleUnknowUser(req,res);
    }
});

//функция неизвестный пользователь
async function handleUnknowUser(req,res) {

    //1.6 Показываем пользователю страницу на которой пользователю предлагается авторизоваться через: GitHub, Яндекс ID или через код, Если URL /
    if (req.path === '/'){
        return res.sendFile(path.join(__dirname,'sait','index.html')); 
    }

    //1.8 Если URL /login с параметром type
    if (req.path === '/login' && req.query.type){
        const type = req.query.type


        //1.8 Генерируем новый токен сессии и новый токен входа
        const newSessionToken = crypto.randomUUID();
        const newLoginToken = crypto.randomUUID();

        const dataToSave = JSON.stringify({
            status: 'Anonymous',
            loginToken: newLoginToken
        });


        //1.8 Делаем запрос Redis чтобы он запомнил токен сессии как ключ, а в качестве значения: статус пользователя: Анонимный и токен входа
        await redisClient.set(newSessionToken,dataToSave);

        //1.8 Web Client делает запрос к модулю Авторизации (указывая токен входа)
        try {
            const response = await axios.post(`${Auth_modul_URL}/auth-request`, {
                type: type,
                login_token: newLoginToken
            });

            //1.8 заопминаем токен сессии в куки
            res.cookie('session_id', newSessionToken, {httpOnly: true});

            //1.8 Редирект на URL от модуля авторизации
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
    //1.7 Если URL /login без параметров или любой другой:
    //Web Client отвечает браузеру редирект на главную;
    return res.redirect('/');
}



//анонимный пользователь
async function handleAnonymousUser(req,res,sessionToken,userData2) {


    //2.7 Если URL /login с параметром type
    if (req.path === '/login' && req.query.type) {
        const type = req.query.type; //како


        //2.7 Генерируем новый токен входа. Токен сессии остаётся прежний
        const newLoginToken =crypto.randomUUID();
        userData2.loginToken = newLoginToken;

        //2.7 Делаем запрос Redis чтобы он обновил токен входа. В качестве ключа используется текущий токен сессии
        await redisClient.set(sessionToken, JSON.stringify(userData2));
        

        //2.7 Web Client делает запрос к модулю Авторизации (указывая токен входа);
        try {
            const response = await axios.post(`${Auth_modul_URL}/auth-request`, {
                type: type,
                login_token: newLoginToken
            });

            //res.cookie('session_id', newSessionToken, {httpOnly: true}); по сценарию токен сессии остается прежним

            if (response.data.redirectURL){
                return res.redirect(response.data.redirectURL);
            } else {
                return res.send('Ошибка: модуль авторизации не прислал ссылку');
            }

        } catch(e) {
            console.error('ошибам связи с модулем авторизации:', e.massage);
            return res.status(500).send('ошибка авторизации');
        }
        //return res.send("перезапуск входа");
    }


    //2.8 и 2.9 Web Client достаёт из ответа от Redis токен входа и делает запрос модулю Авторизации отправляя токен входа для проверки
    try {
        const authResponse = await axios.post(`${Auth_modul_URL}/check-token`, {
            login_token: userData2.loginToken
        });


        //2.11 и 2.12 Если ответ от модуля Авторизации: не опознанный токен или время действия токена закончилось или ответ от модуля Авторизации
        if (authResponse.data.status === 'error' || authResponse.data.status === 'denied') {
            //2.11 и 2.12 Web Client делает запрос Redis, чтобы он удалил текущий ключ. В качестве ключа используется токен сессии. Пользователь переходит в статус Неизвестный
            await redisClient.del(sessionToken);
            //2.11 и 2.12  Web Client отвечает браузеру редирект на главную;
            return res.redirect('/');
        }



        //2.13 Если ответ от модуля Авторизации: доступ предоставлен (пользователь нажал Да во время входа), то
        if (authResponse.data.status === 'success') {
            
            //2.13 Web Client проверяет, что в ответе от модуля авторизации присутствуют 2 JWT токена: токен доступа (Access Token) и токен обновления (Refresh Token);
            const { accessToken, refreshToken } = authResponse.data;


            if (!accessToken || !refreshToken) {
                console.error("токенов нет");
                return res.status(500).send("токены не получены");
            }


            //2.13 Они присутствуют. Web Client меняет статус пользователя на Авторизованный и делает запрос Redis сохранить новый статус пользователя и оба JWT токена (токен входа больше не нужен). В качестве ключа используется токен сессии;
            const newUserData = {
                status: 'Authorized',
                accessToken: accessToken,
                refreshToken: refreshToken
            };

            //2.13 
            await redisClient.set(sessionToken, JSON.stringify(newUserData));
            return res.redirect(req.originalUrl);
        }
        
        return res.send("<h1>ожидайте подтверждения входа</h1><script>setTimeout(() => location.reload(), 2000);</script>");

    } catch (e) {
        console.log("модуль авторизации вернул ошибку");
        return res.redirect('/');
    }
}


//авторизованный пользователь
async function handleAuthorizedUser(req,res, sessionToken, userData2) {

    //3.7 Web Client отвечает браузеру страницей личного кабинета пользователя (информация о пользователе, список его дисциплин, и т.д.);
    if (req.path === '/') {
        return res.sendFile(path.join(__dirname, 'sait', 'index2.html'));
    }

    //3.8 Если URL /login не важно с параметром или без: Web Client отвечает браузеру редирект на главную /;
    if (req.path.startsWith('/login')) {
        return res.redirect('/');
    }


    //3.9 Если URL /logout без параметров (выйти из системы на этом устройстве):
    if (req.path === '/logout') {
        //3.9 Web Client делает запрос к компоненту Redis и просит удалить ключ. В качестве ключа используется токен сессии. Пользователь переходит в статус Неизвестный
        const logoutall = req.query.all === 'true'; //3.10
        await redisClient.del(sessionToken);
        res.clearCookie('session_id');


        //3.10 Если URL /logout с параметром all=true (выйти из системы на всех устройствах):
        if (logoutall) {
            try {
                await axios.post(`${Auth_modul_URL}/logout`, { refreshToken: userData2.refreshToken});
            } catch (e) {console.error('Ошибка на сервере авторизации'); }
        }
        //3.9 Web Client отвечает браузеру редирект на главную /;
        return res.redirect('/');
    }

    //3.11
    try {
        const mainModulURL = Main_module_URL;
        const response = await axios({
            method: req.method,
            url: mainModulURL,
            headers: { 
                'Authorization': 'Bearer ${userData2.accessToken}' //передача токена доступа
            },
            data: req.body
        });

        return res.send(response.data); //ответ браузеру данными

    } catch (error) {
        if (!error.response) {
            return res.status(500).send("ошибка главного модуля");
        }

        const status = error.response.status;



        //Главный модуль отвечает 403 кодом; Web Client формирует страницу с сообщением об отсутствии доступа и отвечает браузеру;
        if (status === 403) {
            return res.status(403).send("у вас нет прав");
        }

        if (status === 401) { //если токен устарел 
            try{
                //запрос к модулю авторизации с токеном обновления 
                const refreshResponse = await axios.post(`${Auth_modul_URL}/refresh`, {refreshToken: userData2.refreshToken});
                const {accessToken, refreshToken} = refreshResponse.data;  //получили новые токены


                //сохраняем их в редис
                await redisClient.set(
                    sessionToken,
                    JSON.stringify({
                        status: 'Authorized',
                        accessToken,
                        refreshToken
                    })
                );
                //повторяем запрос 
                const retryResponse = await axios({
                    method: req.method,
                    url: mainModulURL,
                    headers: { 
                        'Authorization': 'Bearer ${accessToken}'
                    },
                    data: req.body
                });
                return res.send(retryResponse.data);
            }catch (error) { //если refresh токен тожн устрарел
                if (error.response && error.response.status === 401){
                    await redisClient.del(sessionToken); //удаляем сессию из редис
                    res.clearCookie('session_id'); //чистим куки
                    return res.redirect('/'); // редирект на главную
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