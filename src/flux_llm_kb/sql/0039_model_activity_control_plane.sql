ALTER TABLE model_activity_events
    DROP CONSTRAINT IF EXISTS model_activity_events_activity_class_check;

ALTER TABLE model_activity_events
    ADD CONSTRAINT model_activity_events_activity_class_check
    CHECK (activity_class IN ('retrieval', 'vision_ocr', 'sidecar', 'health', 'control_plane', 'model_loading'));
