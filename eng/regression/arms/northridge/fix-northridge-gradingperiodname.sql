-- Northridge v73 (20241218) backup ships all 78 edfi.GradingPeriod rows with GradingPeriodName = '' (required key in DS 5.2).
-- The target API rejects them (400) and everything downstream (sessions, courseOfferings, sections, enrollments, grades) cascades into 409s.
-- Applied 2026-09-02 to EdFi_Ods_Northridge on the host: name := grading period descriptor CodeValue (unique per key).

SET XACT_ABORT ON;
BEGIN TRANSACTION;
ALTER TABLE edfi.Grade NOCHECK CONSTRAINT ALL;
ALTER TABLE edfi.SessionGradingPeriod NOCHECK CONSTRAINT ALL;
ALTER TABLE edfi.GradebookEntry NOCHECK CONSTRAINT ALL;

UPDATE gp SET GradingPeriodName = d.CodeValue
FROM edfi.GradingPeriod gp JOIN edfi.Descriptor d ON d.DescriptorId = gp.GradingPeriodDescriptorId
WHERE gp.GradingPeriodName = '';
PRINT CONCAT('GradingPeriod updated: ', @@ROWCOUNT);

UPDATE s SET GradingPeriodName = d.CodeValue
FROM edfi.SessionGradingPeriod s JOIN edfi.Descriptor d ON d.DescriptorId = s.GradingPeriodDescriptorId
WHERE s.GradingPeriodName = '';
PRINT CONCAT('SessionGradingPeriod updated: ', @@ROWCOUNT);

UPDATE g SET GradingPeriodName = d.CodeValue
FROM edfi.Grade g JOIN edfi.Descriptor d ON d.DescriptorId = g.GradingPeriodDescriptorId
WHERE g.GradingPeriodName = '';
PRINT CONCAT('Grade updated: ', @@ROWCOUNT);

ALTER TABLE edfi.GradebookEntry WITH CHECK CHECK CONSTRAINT ALL;
ALTER TABLE edfi.SessionGradingPeriod WITH CHECK CHECK CONSTRAINT ALL;
ALTER TABLE edfi.Grade WITH CHECK CHECK CONSTRAINT ALL;
COMMIT TRANSACTION;
PRINT 'COMMITTED';
GO
SET NOCOUNT ON;
SELECT 'GradingPeriod' AS T, SUM(CASE WHEN GradingPeriodName='' THEN 1 ELSE 0 END) AS EmptyLeft, COUNT(*) AS Total FROM edfi.GradingPeriod
UNION ALL SELECT 'SessionGradingPeriod', SUM(CASE WHEN GradingPeriodName='' THEN 1 ELSE 0 END), COUNT(*) FROM edfi.SessionGradingPeriod
UNION ALL SELECT 'Grade', SUM(CASE WHEN GradingPeriodName='' THEN 1 ELSE 0 END), COUNT(*) FROM edfi.Grade;
SELECT COUNT(*) AS UntrustedFKs FROM sys.foreign_keys WHERE is_not_trusted = 1 AND parent_object_id IN (OBJECT_ID('edfi.Grade'), OBJECT_ID('edfi.SessionGradingPeriod'), OBJECT_ID('edfi.GradebookEntry'));
SELECT TOP 3 GradingPeriodName, PeriodSequence, SchoolId FROM edfi.GradingPeriod ORDER BY SchoolId, GradingPeriodName;
GO
