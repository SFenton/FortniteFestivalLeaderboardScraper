import { useCallback, useEffect, useId, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { FeedbackKind, FeedbackStatusResponse } from '@festival/core/api';
import { Colors, Font, Gap, Weight, Radius, GeneralSize, Opacity, CssValue, LineHeight, Border, Display, flexColumn, btnPrimary, border, padding } from '@festival/theme';
import { api, ApiFeedbackError } from '../../../api/client';
import Modal from '../../../components/modals/Modal';
import ConfirmAlert from '../../../components/modals/ConfirmAlert';
import PressableButton from '../../../components/common/PressableButton';
import { APP_VERSION } from '../../../hooks/data/useVersions';

const MAX_ATTACHMENTS = 4;
const MAX_REQUEST_BYTES = 94_371_840;
const POLL_INTERVAL_MS = 1_500;
const POLL_TIMEOUT_MS = 5 * 60 * 1_000;

type AttachmentPreview = {
  id: string;
  file: File;
  url: string;
  mediaKind: 'image' | 'video';
};

type SubmissionPhase = 'idle' | 'uploading' | 'polling' | 'success' | 'error';

type FeedbackModalProps = {
  kind: FeedbackKind;
  visible: boolean;
  onClose: () => void;
};

function prefixFor(kind: FeedbackKind): string {
  return kind === 'bug' ? '[Bug] ' : '[Feature] ';
}

function isTerminalStatus(status: FeedbackStatusResponse['status']) {
  return status === 'submitted' || status === 'failed';
}

function delay(ms: number): Promise<void> {
  return new Promise(resolve => window.setTimeout(resolve, ms));
}

export default function FeedbackModal({ kind, visible, onClose }: FeedbackModalProps) {
  const { t } = useTranslation(['translation', 'settings'], { nsMode: 'fallback' });
  const prefix = prefixFor(kind);
  const titleId = useId();
  const descriptionId = useId();
  const reproId = useId();
  const expectedId = useId();
  const fileInputId = useId();
  const descriptionHintId = useId();
  const reproHintId = useId();
  const expectedHintId = useId();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const attachmentsRef = useRef<AttachmentPreview[]>([]);
  const cancelledRef = useRef(false);
  const [title, setTitle] = useState(prefix);
  const [description, setDescription] = useState('');
  const [repro, setRepro] = useState('');
  const [expected, setExpected] = useState('');
  const [attachments, setAttachments] = useState<AttachmentPreview[]>([]);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [phase, setPhase] = useState<SubmissionPhase>('idle');
  const [uploadProgress, setUploadProgress] = useState(0);
  const [resultMessage, setResultMessage] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const st = useStyles();

  useEffect(() => {
    attachmentsRef.current = attachments;
  }, [attachments]);

  useEffect(() => () => {
    cancelledRef.current = true;
    for (const attachment of attachmentsRef.current) URL.revokeObjectURL(attachment.url);
  }, []);

  useEffect(() => {
    if (!visible) return;
    cancelledRef.current = false;
  }, [visible]);

  const titleBody = title.startsWith(prefix) ? title.slice(prefix.length) : title;
  const canSubmit = titleBody.trim().length > 0 && description.trim().length > 0 && phase !== 'uploading' && phase !== 'polling';
  const submitted = phase === 'success';
  const isDirty = !submitted && (
    title !== prefix
    || description.trim().length > 0
    || repro.trim().length > 0
    || expected.trim().length > 0
    || attachments.length > 0
  );
  const totalAttachmentBytes = useMemo(() => attachments.reduce((total, item) => total + item.file.size, 0), [attachments]);

  const closeNow = useCallback(() => {
    setConfirmOpen(false);
    onClose();
  }, [onClose]);

  const handleClose = useCallback(() => {
    if (isDirty) {
      setConfirmOpen(true);
      return;
    }
    closeNow();
  }, [closeNow, isDirty]);

  const removeAttachment = useCallback((id: string) => {
    setAttachmentError(null);
    setAttachments(current => {
      const removed = current.find(item => item.id === id);
      if (removed) URL.revokeObjectURL(removed.url);
      return current.filter(item => item.id !== id);
    });
  }, []);

  const handleAttachmentClick = useCallback((url: string) => {
    window.open(url, '_blank', 'noopener');
  }, []);

  const handleAttachFiles = useCallback((files: FileList | null) => {
    if (fileInputRef.current) fileInputRef.current.value = '';
    if (!files?.length) return;

    const nextFiles = Array.from(files);
    if (attachments.length + nextFiles.length > MAX_ATTACHMENTS) {
      setAttachmentError(t('settings.feedback.errors.tooManyAttachments', { count: MAX_ATTACHMENTS }));
      return;
    }

    const unsupported = nextFiles.find(file => !file.type.startsWith('image/') && !file.type.startsWith('video/'));
    if (unsupported) {
      setAttachmentError(t('settings.feedback.errors.unsupportedMedia'));
      return;
    }

    const nextTotal = totalAttachmentBytes + nextFiles.reduce((total, file) => total + file.size, 0);
    if (nextTotal > MAX_REQUEST_BYTES) {
      setAttachmentError(t('settings.feedback.errors.payloadTooLarge', { size: '90 MiB' }));
      return;
    }

    setAttachmentError(null);
    setAttachments(current => [
      ...current,
      ...nextFiles.map(file => ({
        id: `${file.name}-${file.size}-${file.lastModified}-${globalThis.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2)}`,
        file,
        url: URL.createObjectURL(file),
        mediaKind: file.type.startsWith('video/') ? 'video' as const : 'image' as const,
      })),
    ]);
  }, [attachments.length, t, totalAttachmentBytes]);

  const buildFormData = useCallback(() => {
    const formData = new FormData();
    formData.append('kind', kind);
    formData.append('platform', 'web');
    formData.append('title', title.trim());
    formData.append('description', description.trim());
    if (kind === 'bug') {
      const reproText = repro.trim();
      const expectedText = expected.trim();
      if (reproText) formData.append('repro', reproText);
      if (expectedText) formData.append('expected', expectedText);
    }
    if (APP_VERSION) formData.append('appVersion', APP_VERSION.slice(0, 64));
    if (typeof navigator !== 'undefined' && navigator.userAgent) {
      formData.append('clientInfo', navigator.userAgent.slice(0, 256));
    }
    for (const attachment of attachments) {
      formData.append('media', attachment.file, attachment.file.name);
    }
    return formData;
  }, [attachments, description, expected, kind, repro, title]);

  const mapError = useCallback((error: unknown) => {
    if (error instanceof ApiFeedbackError) {
      if (error.status === 429) {
        return error.retryAfter
          ? t('settings.feedback.errors.rateLimitedWithRetry', { seconds: error.retryAfter })
          : t('settings.feedback.errors.rateLimited');
      }
      if (error.code === 'payload_too_large') return t('settings.feedback.errors.payloadTooLarge', { size: '90 MiB' });
      if (error.code === 'too_many_attachments') return t('settings.feedback.errors.tooManyAttachments', { count: MAX_ATTACHMENTS });
      if (error.code === 'unsupported_media') return t('settings.feedback.errors.unsupportedMedia');
      if (error.code === 'feedback_disabled') return t('settings.feedback.errors.disabled');
      if (error.code === 'feedback_busy' || error.status === 503) return t('settings.feedback.errors.busy');
      if (error.code === 'title_required') return t('settings.feedback.errors.titleRequired');
      if (error.code === 'description_required') return t('settings.feedback.errors.descriptionRequired');
      if (error.code === 'field_too_long') return t('settings.feedback.errors.fieldTooLong');
      if (error.status === 400) return t('settings.feedback.errors.invalidForm');
    }
    return t('settings.feedback.errors.generic');
  }, [t]);

  const pollStatus = useCallback(async (id: string) => {
    const deadline = Date.now() + POLL_TIMEOUT_MS;
    while (!cancelledRef.current && Date.now() < deadline) {
      const status = await api.getFeedbackStatus(id);
      if (isTerminalStatus(status.status)) return status;
      await delay(POLL_INTERVAL_MS);
    }
    throw new Error('feedback_poll_timeout');
  }, []);

  const handleSubmit = useCallback(async () => {
    if (!canSubmit || phase === 'success') {
      if (phase === 'success') closeNow();
      return;
    }
    setPhase('uploading');
    setUploadProgress(0);
    setErrorMessage(null);
    setResultMessage(null);
    setAttachmentError(null);
    try {
      const accepted = await api.submitFeedback(buildFormData(), progress => setUploadProgress(progress));
      if (cancelledRef.current) return;
      setUploadProgress(100);
      setPhase('polling');
      const status = await pollStatus(accepted.id);
      if (cancelledRef.current) return;
      if (status.status === 'submitted') {
        const skipped = status.attachments.filter(attachment => attachment.outcome === 'skipped').length;
        const filedMessage = status.issueNumber != null
          ? t('settings.feedback.successWithIssue', { issueNumber: status.issueNumber })
          : t('settings.feedback.successWithReference', { id: status.id });
        setResultMessage(skipped > 0
          ? `${filedMessage} ${t('settings.feedback.attachmentsSkipped', { count: skipped })}`
          : filedMessage);
        setPhase('success');
        return;
      }
      setErrorMessage(status.error || t('settings.feedback.errors.failedStatus'));
      setPhase('error');
    } catch (error) {
      if (cancelledRef.current) return;
      setErrorMessage(error instanceof Error && error.message === 'feedback_poll_timeout'
        ? t('settings.feedback.errors.timeout')
        : mapError(error));
      setPhase('error');
    }
  }, [buildFormData, canSubmit, closeNow, mapError, phase, pollStatus, t]);

  const titleText = kind === 'bug' ? t('settings.feedback.reportTitle') : t('settings.feedback.featureTitle');
  const submitLabel = phase === 'success' ? t('common.close') : phase === 'uploading' || phase === 'polling' ? t('settings.feedback.submitting') : t('settings.feedback.submit');

  return (
    <Modal
      visible={visible}
      title={titleText}
      onClose={handleClose}
      onApply={handleSubmit}
      applyLabel={submitLabel}
      applyDisabled={phase !== 'success' && !canSubmit}
      panelTestId={`settings-feedback-${kind}-modal`}
      afterPanel={confirmOpen ? (
        <ConfirmAlert
          title={t('settings.feedback.discardTitle')}
          message={t('settings.feedback.discardMessage')}
          noLabel={t('settings.feedback.keepEditing')}
          yesLabel={t('settings.feedback.discard')}
          onNo={() => setConfirmOpen(false)}
          onYes={closeNow}
          onExitComplete={() => setConfirmOpen(false)}
        />
      ) : null}
    >
      <div style={st.form}>
        <label htmlFor={titleId} style={st.label}>{t('settings.feedback.titleLabel')}</label>
        <input
          id={titleId}
          value={title}
          maxLength={200}
          onChange={event => setTitle(event.target.value)}
          style={st.input}
          data-testid={`settings-feedback-${kind}-title`}
        />

        <label htmlFor={descriptionId} style={st.label}>{t('settings.feedback.descriptionLabel')}</label>
        <div id={descriptionHintId} style={st.hint}>{kind === 'bug' ? t('settings.feedback.bugDescriptionHint') : t('settings.feedback.featureDescriptionHint')}</div>
        <textarea
          id={descriptionId}
          value={description}
          maxLength={10_000}
          rows={5}
          aria-describedby={descriptionHintId}
          onChange={event => setDescription(event.target.value)}
          style={st.textarea}
          data-testid={`settings-feedback-${kind}-description`}
        />

        {kind === 'bug' && (
          <>
            <label htmlFor={reproId} style={st.label}>{t('settings.feedback.reproLabel')}</label>
            <div id={reproHintId} style={st.hint}>{t('settings.feedback.reproHint')}</div>
            <textarea
              id={reproId}
              value={repro}
              maxLength={10_000}
              rows={4}
              aria-describedby={reproHintId}
              placeholder={t('settings.feedback.reproPlaceholder')}
              onChange={event => setRepro(event.target.value)}
              style={st.textarea}
              data-testid="settings-feedback-bug-repro"
            />
            <label htmlFor={expectedId} style={st.label}>{t('settings.feedback.expectedLabel')}</label>
            <div id={expectedHintId} style={st.hint}>{t('settings.feedback.expectedHint')}</div>
            <textarea
              id={expectedId}
              value={expected}
              maxLength={10_000}
              rows={4}
              aria-describedby={expectedHintId}
              onChange={event => setExpected(event.target.value)}
              style={st.textarea}
              data-testid="settings-feedback-bug-expected"
            />
          </>
        )}

        {attachments.length > 0 && (
          <div style={st.attachments} aria-label={t('settings.feedback.attachmentsLabel')}>
            {attachments.map(attachment => (
              <div key={attachment.id} style={st.attachmentCard}>
                <button
                  type="button"
                  style={st.thumbnailButton}
                  aria-label={t('settings.feedback.openAttachment', { name: attachment.file.name })}
                  onClick={() => handleAttachmentClick(attachment.url)}
                  data-testid="settings-feedback-attachment-thumbnail"
                >
                  {attachment.mediaKind === 'image' ? (
                    <img src={attachment.url} alt={attachment.file.name} style={st.thumbnailMedia} />
                  ) : (
                    <video src={attachment.url} preload="metadata" muted aria-label={attachment.file.name} style={st.thumbnailMedia} />
                  )}
                </button>
                <div style={st.attachmentName}>{attachment.file.name}</div>
                <button
                  type="button"
                  style={st.removeAttachmentButton}
                  aria-label={t('settings.feedback.removeAttachment', { name: attachment.file.name })}
                  onClick={() => removeAttachment(attachment.id)}
                >
                  ×
                </button>
              </div>
            ))}
          </div>
        )}

        <input
          ref={fileInputRef}
          id={fileInputId}
          type="file"
          accept="image/*,video/*"
          multiple
          hidden
          onChange={event => handleAttachFiles(event.currentTarget.files)}
          data-testid={`settings-feedback-${kind}-file-input`}
        />
        <PressableButton
          style={attachments.length >= MAX_ATTACHMENTS ? st.attachButtonDisabled : st.attachButton}
          onPress={() => fileInputRef.current?.click()}
          disabled={attachments.length >= MAX_ATTACHMENTS || phase === 'uploading' || phase === 'polling'}
        >
          {t('settings.feedback.attachMedia')}
        </PressableButton>
        <div style={st.hint}>{t('settings.feedback.attachHint', { count: MAX_ATTACHMENTS, size: '90 MiB' })}</div>
        {attachmentError && <div role="alert" style={st.errorText}>{attachmentError}</div>}

        {(phase === 'uploading' || phase === 'polling') && (
          <div role="status" aria-live="polite" style={st.statusBlock}>
            {phase === 'uploading' ? (
              <>
                <div style={st.statusText}>{t('settings.feedback.uploading', { progress: uploadProgress })}</div>
                <progress value={uploadProgress} max={100} aria-label={t('settings.feedback.uploadProgressLabel')} style={st.progress} />
              </>
            ) : (
              <div style={st.statusText}>{t('settings.feedback.polling')}</div>
            )}
          </div>
        )}
        {resultMessage && <div role="status" aria-live="polite" style={st.successText}>{resultMessage}</div>}
        {errorMessage && <div role="alert" style={st.errorText}>{errorMessage}</div>}
      </div>
    </Modal>
  );
}

function useStyles() {
  return useMemo(() => ({
    form: {
      ...flexColumn,
      gap: Gap.md,
    } as CSSProperties,
    label: {
      fontSize: Font.md,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
    } as CSSProperties,
    hint: {
      fontSize: Font.sm,
      color: Colors.textSecondary,
      lineHeight: LineHeight.snug,
    } as CSSProperties,
    input: {
      minHeight: GeneralSize.thumb,
      borderRadius: Radius.sm,
      border: border(Border.thin, Colors.borderPrimary),
      background: Colors.surfaceElevated,
      color: Colors.textPrimary,
      padding: padding(Gap.md),
      fontSize: Font.md,
      boxSizing: 'border-box',
      width: CssValue.full,
    } as CSSProperties,
    textarea: {
      minHeight: 116,
      borderRadius: Radius.sm,
      border: border(Border.thin, Colors.borderPrimary),
      background: Colors.surfaceElevated,
      color: Colors.textPrimary,
      padding: padding(Gap.md),
      fontSize: Font.md,
      lineHeight: LineHeight.snug,
      boxSizing: 'border-box',
      width: CssValue.full,
      resize: 'vertical',
    } as CSSProperties,
    attachments: {
      display: Display.grid,
      gridTemplateColumns: 'repeat(auto-fit, minmax(132px, 1fr))',
      gap: Gap.md,
    } as CSSProperties,
    attachmentCard: {
      position: 'relative',
      minWidth: 0,
      borderRadius: Radius.sm,
      border: border(Border.thin, Colors.borderSubtle),
      background: Colors.surfaceSubtle,
      padding: Gap.sm,
      ...flexColumn,
      gap: Gap.xs,
    } as CSSProperties,
    thumbnailButton: {
      width: CssValue.full,
      minHeight: 96,
      border: CssValue.none,
      borderRadius: Radius.xs,
      background: Colors.surfaceMuted,
      padding: 0,
      cursor: 'pointer',
      overflow: 'hidden',
    } as CSSProperties,
    thumbnailMedia: {
      width: CssValue.full,
      height: 96,
      objectFit: 'cover',
      display: 'block',
    } as CSSProperties,
    attachmentName: {
      color: Colors.textSecondary,
      fontSize: Font.sm,
      overflowWrap: 'anywhere',
    } as CSSProperties,
    removeAttachmentButton: {
      position: 'absolute',
      top: Gap.xs,
      right: Gap.xs,
      minWidth: GeneralSize.thumb,
      minHeight: GeneralSize.thumb,
      borderRadius: CssValue.circle,
      border: border(Border.thin, Colors.borderPrimary),
      background: Colors.surfaceElevated,
      color: Colors.textPrimary,
      cursor: 'pointer',
      fontSize: Font.lg,
      lineHeight: 1,
    } as CSSProperties,
    attachButton: {
      ...btnPrimary,
      minHeight: GeneralSize.thumb,
      padding: padding(Gap.md, Gap.xl),
      alignSelf: 'flex-start',
    } as CSSProperties,
    attachButtonDisabled: {
      ...btnPrimary,
      minHeight: GeneralSize.thumb,
      padding: padding(Gap.md, Gap.xl),
      alignSelf: 'flex-start',
      opacity: Opacity.faded,
      cursor: 'not-allowed',
    } as CSSProperties,
    statusBlock: {
      ...flexColumn,
      gap: Gap.sm,
    } as CSSProperties,
    statusText: {
      color: Colors.textSecondary,
      fontSize: Font.md,
    } as CSSProperties,
    progress: {
      width: CssValue.full,
      minHeight: 14,
    } as CSSProperties,
    successText: {
      color: Colors.statusGreen,
      fontSize: Font.md,
      lineHeight: LineHeight.snug,
    } as CSSProperties,
    errorText: {
      color: Colors.statusRed,
      fontSize: Font.md,
      lineHeight: LineHeight.snug,
    } as CSSProperties,
  }), []);
}
