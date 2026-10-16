/**
 * Attachment — mirrors @1js/fai-react-attachments Attachment anatomy.
 *
 * Slots mirrored: root, media (icon/thumbnail), content (file name),
 *   primaryAction (click → open), dismissButton (remove), dismissIcon.
 */
import { Dismiss12Regular, DocumentRegular } from "@fluentui/react-icons";
import { useAttachmentStyles } from "./copilotStyles";
import { mergeClasses } from "@fluentui/react-components";

export interface AttachmentProps {
  id: string;
  /** Display name of the attached file or reference */
  name: string;
  /** MIME type or semantic type label */
  type?: string;
  /** Optional icon override for the media slot */
  icon?: React.ReactNode;
  /** Called when the attachment name is clicked (open/preview) */
  onOpen?: () => void;
  /** Called when the dismiss button is clicked (remove) */
  onRemove?: () => void;
  className?: string;
}

export function Attachment({
  name,
  icon,
  onOpen,
  onRemove,
  className,
}: AttachmentProps) {
  const styles = useAttachmentStyles();
  return (
    <div
      className={mergeClasses(styles.root, className)}
      role="listitem"
    >
      {/* slot: media */}
      <span className={styles.media} aria-hidden>
        {icon ?? <DocumentRegular fontSize={12} />}
      </span>

      {/* slot: content / primaryAction */}
      <button
        className={styles.content}
        onClick={onOpen}
        type="button"
        style={{ background: "none", border: "none", padding: 0, cursor: onOpen ? "pointer" : "default" }}
      >
        {name}
      </button>

      {/* slot: dismissButton */}
      {onRemove && (
        <button
          className={styles.dismissButton}
          onClick={onRemove}
          type="button"
          aria-label={`Remove ${name}`}
        >
          {/* slot: dismissIcon */}
          <Dismiss12Regular />
        </button>
      )}
    </div>
  );
}

/** A horizontal list of attachment chips inside the Composer */
export interface AttachmentListProps {
  attachments: AttachmentProps[];
  className?: string;
}
export function AttachmentList({ attachments, className }: AttachmentListProps) {
  const styles = useAttachmentStyles();
  if (!attachments.length) return null;
  return (
    <div
      className={mergeClasses(styles.root, className)}
      role="list"
      style={{ flexWrap: "wrap", gap: "4px", padding: 0, border: "none", backgroundColor: "transparent" }}
    >
      {attachments.map((a) => (
        <Attachment key={a.id} {...a} />
      ))}
    </div>
  );
}
