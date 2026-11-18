'use strict';
const {run} = require('./extension.cjs');
exports.run = async () => {
    const output = await run();
    console.log('Portia editor replay: ' + output);
};
