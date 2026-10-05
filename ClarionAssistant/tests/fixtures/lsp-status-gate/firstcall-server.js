// Scripted server for the c7878eba run of LspDiagnostics.StatusGateTest.ps1, staged as server.js next to
// fake-lsp-server.js. Same behaviour; the only difference is that the status goes out the way the real
// server sends it, which is what LspClient.Start looks for in server.js to wait for the status from the
// first call. fake-lsp-server.js itself must NOT contain that spelling: its nostatus run stands for an
// older server and has to stay on the fallback.
'use strict';
const fake = require('./fake-lsp-server.js');
const connection = { sendNotification: fake.notify };
fake.setStatusSender(p => connection.sendNotification('clarion/diagnosticsStatus', p));
