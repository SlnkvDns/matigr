import assert from 'node:assert/strict';
import {sql,quote as q,eventually,compose,databaseBarrier} from './transport-test-support.mjs';
const check=await sql('checking',"SELECT check_id FROM checks WHERE kind='Appeal' AND state='Assigned' ORDER BY created_at DESC LIMIT 1");assert.ok(check);
const release=await databaseBarrier('results',730504);
try {
 await sql('results',`CREATE FUNCTION matigr_appeal_sent_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.status='Sent' AND NEW.envelope->'payload'->>'evaluationId'=${q(check)} THEN PERFORM pg_advisory_xact_lock(730504); END IF; RETURN NEW; END $$; CREATE TRIGGER matigr_appeal_sent_fault BEFORE UPDATE ON outbox FOR EACH ROW EXECUTE FUNCTION matigr_appeal_sent_fault();`);
 console.log('Armed real Results confirm→Sent barrier; complete the selected Appeal in browser now.');
 await eventually(()=>sql('results',"SELECT count(*) FROM pg_stat_activity WHERE wait_event='advisory' AND query LIKE 'UPDATE outbox SET status%'"),x=>Number(x)>0,'Applied confirm before Sent crash',180000);
 const receipt=await sql('results',`SELECT response::text FROM evaluation_receipts WHERE evaluation_id=${q(check)}`);assert.ok(receipt);
 const message=JSON.parse(receipt).messageId;
 compose(['kill','-s','SIGKILL','results-api']);await release();
 await sql('results','DROP TRIGGER matigr_appeal_sent_fault ON outbox; DROP FUNCTION matigr_appeal_sent_fault();');compose(['start','results-api']);
 await eventually(()=>sql('results',`SELECT status FROM outbox WHERE message_id=${q(message)}`),x=>x==='Sent','same receipt recovers lease',90000);
 assert.equal(await sql('results',`SELECT count(*) FROM evaluation_receipts WHERE evaluation_id=${q(check)}`),'1');
 assert.equal(await sql('results',`SELECT count(*) FROM outbox WHERE message_id=${q(message)}`),'1');
 console.log('Results SIGKILL after actual broker confirm before Sent: same receipt/message/publication recovered, one evaluation: OK');
} finally {await release().catch(()=>{});await sql('results','DROP TRIGGER IF EXISTS matigr_appeal_sent_fault ON outbox; DROP FUNCTION IF EXISTS matigr_appeal_sent_fault();');compose(['start','results-api']);}
