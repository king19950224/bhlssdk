-- 0030: audit_log 库级 append-only（plan §9.5）
-- 此前只靠应用约定不 UPDATE/DELETE；任何直连 DB 的账号都能改历史。
-- 用触发器拒绝变更/删除；允许应用角色继续 INSERT。
-- 部署：按序号执行本文件（与既有 migrate 脚本一致）。

CREATE OR REPLACE FUNCTION forbid_audit_log_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'audit_log 为 append-only，禁止 %（plan §9.5）', TG_OP
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$;

DROP TRIGGER IF EXISTS trg_audit_log_no_update ON audit_log;
CREATE TRIGGER trg_audit_log_no_update
    BEFORE UPDATE ON audit_log
    FOR EACH ROW
    EXECUTE PROCEDURE forbid_audit_log_mutation();

DROP TRIGGER IF EXISTS trg_audit_log_no_delete ON audit_log;
CREATE TRIGGER trg_audit_log_no_delete
    BEFORE DELETE ON audit_log
    FOR EACH ROW
    EXECUTE PROCEDURE forbid_audit_log_mutation();

COMMENT ON FUNCTION forbid_audit_log_mutation() IS
    'audit_log append-only：拒绝 UPDATE/DELETE，仅允许 INSERT';
