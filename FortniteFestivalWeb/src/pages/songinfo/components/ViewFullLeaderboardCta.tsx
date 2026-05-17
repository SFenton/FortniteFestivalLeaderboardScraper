import { useMemo, type CSSProperties, type ReactNode, type Ref } from 'react';
import { Link, type LinkProps } from 'react-router-dom';
import { Align, Colors, Cursor, CssProp, Display, FAST_FADE_MS, Font, Gap, Justify, Layout, Radius, TextAlign, Weight, WhiteSpace, WordBreak, frostedCard, padding, transition } from '@festival/theme';

type ViewFullLeaderboardCtaBaseProps = {
  children: ReactNode;
  compact?: boolean;
  componentRef?: (element: HTMLElement | null) => void;
  style?: CSSProperties;
};

type ViewFullLeaderboardCtaDivProps = ViewFullLeaderboardCtaBaseProps &
  Omit<React.HTMLAttributes<HTMLDivElement>, keyof ViewFullLeaderboardCtaBaseProps | 'children'> & {
    to?: undefined;
  };

type ViewFullLeaderboardCtaLinkProps = ViewFullLeaderboardCtaBaseProps &
  Omit<LinkProps, keyof ViewFullLeaderboardCtaBaseProps | 'children'> & {
    to: LinkProps['to'];
  };

type ViewFullLeaderboardCtaProps = ViewFullLeaderboardCtaDivProps | ViewFullLeaderboardCtaLinkProps;

export default function ViewFullLeaderboardCta(props: ViewFullLeaderboardCtaProps) {
  const styles = useViewFullLeaderboardCtaStyles();
  const { children, compact, componentRef, style, ...rest } = props;
  const combinedStyle = {
    ...(compact ? styles.compact : styles.base),
    ...style,
  };

  if (props.to !== undefined) {
    const linkProps = rest as Omit<ViewFullLeaderboardCtaLinkProps, keyof ViewFullLeaderboardCtaBaseProps | 'children'>;
    return (
      <Link
        {...linkProps}
        ref={componentRef as Ref<HTMLAnchorElement> | undefined}
        style={combinedStyle}
      >
        {children}
      </Link>
    );
  }

  const divProps = rest as Omit<ViewFullLeaderboardCtaDivProps, keyof ViewFullLeaderboardCtaBaseProps | 'children'>;
  return (
    <div
      {...divProps}
      ref={componentRef as Ref<HTMLDivElement> | undefined}
      style={combinedStyle}
    >
      {children}
    </div>
  );
}

function useViewFullLeaderboardCtaStyles() {
  return useMemo(() => {
    const base = {
      ...frostedCard,
      display: Display.flex,
      position: 'relative',
      alignItems: Align.center,
      justifyContent: Justify.center,
      minHeight: Layout.entryRowHeight,
      padding: padding(Gap.sm, Gap.md),
      borderRadius: Radius.md,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      cursor: Cursor.pointer,
      textAlign: TextAlign.center,
      textDecoration: 'none',
      whiteSpace: WhiteSpace.nowrap,
      wordBreak: WordBreak.normal,
      transition: transition(CssProp.backgroundColor, FAST_FADE_MS),
    } as CSSProperties;

    return {
      base,
      compact: {
        ...base,
        flexDirection: 'column',
        gap: Gap.xs,
      } as CSSProperties,
    };
  }, []);
}