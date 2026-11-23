import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {compose,sql,eventually,quote as q} from './transport-test-support.mjs';
const id=JSON.parse(await readFile('.local/e2e/manual-end-operation.json')).reply.appealId;
const keepAlive=setInterval(()=>{},1000);
try {
 await eventually(()=>sql('appeals',`SELECT state FROM appeals WHERE appeal_id=${q(id)}`),x=>x==='RecheckPending','post-end manual retry assigned');
 assert.equal(await sql('appeals',`SELECT count(*) FROM selection_attempts WHERE appeal_id=${q(id)}`),'2');
 assert.equal(await sql('checking',`SELECT count(*) FROM checks WHERE appeal_id=${q(id)}`),'1');
 const assignment=await sql('checking',"SELECT json_agg(row_to_json(a))::text FROM assignments a WHERE state='Active'");assert.notEqual(assignment,'');
 const window=await sql('appeals','SELECT json_agg(row_to_json(w))::text FROM submission_windows w');
 compose(['restart','rabbitmq','checking-api','results-api','appeals-api']);
 for(const port of [5101,5102,5103]) await eventually(async()=>{try{return(await fetch(`http://127.0.0.1:${port}/health/ready`,{signal:AbortSignal.timeout(2000)})).status}catch{return 0}},x=>x===200,'API and broker recovered',90000);
 assert.equal(await sql('checking',"SELECT json_agg(row_to_json(a))::text FROM assignments a WHERE state='Active'"),assignment);
 assert.equal(await sql('appeals','SELECT json_agg(row_to_json(w))::text FROM submission_windows w'),window);
 assert.equal(await sql('appeals',`SELECT count(*) FROM selection_attempts WHERE appeal_id=${q(id)}`),'2');
 console.log('Post-end all APIs+Rabbit restart: exact active assignment/window unchanged, two manual attempts and one Appeal Check: OK');
} finally {clearInterval(keepAlive);}
