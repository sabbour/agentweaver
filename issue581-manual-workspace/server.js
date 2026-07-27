const http = require('http');
const server = http.createServer((req, res) => { res.statusCode = 200; res.end('ok'); });
server.listen(43111, '0.0.0.0', () => console.log('LISTENING ON PORT 43111'));
