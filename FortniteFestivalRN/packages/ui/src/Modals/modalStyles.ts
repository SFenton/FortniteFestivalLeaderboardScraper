import {StyleSheet} from 'react-native';
import {Colors, Radius, Font, LineHeight, Gap, Opacity, Size} from '../theme';

export const modalStyles = StyleSheet.create({
  modalCard: {
    padding: 14,
    gap: Gap.xl,
    width: '100%',
    maxWidth: 640,
    alignSelf: 'center',
  },
  modalCardMobile: {
    padding: 0,
    borderBottomLeftRadius: 0,
    borderBottomRightRadius: 0,
  },
  modalCardWindows: {
    flex: 1,
    maxWidth: undefined,
    borderRadius: 0,
  },
  modalHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  modalHeaderPinned: {
    paddingHorizontal: 14,
    paddingTop: 14,
    paddingBottom: Gap.section,
  },
  modalScrollContent: {
    flex: 1,
  },
  modalScrollInner: {
    paddingHorizontal: 14,
    gap: Gap.section,
  },
  modalTitle: {
    color: Colors.textPrimary,
    fontSize: Font.title,
    fontWeight: '800',
  },
  modalClose: {
    paddingHorizontal: Gap.lg,
    paddingVertical: Gap.md,
    borderRadius: Radius.sm,
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
  },
  modalCloseText: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
    fontWeight: '700',
  },
  modalSection: {
    gap: Gap.md,
  },
  modalSectionTitle: {
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: '800',
  },
  modalHint: {
    color: Colors.textSecondary,
    opacity: Opacity.pressed,
    fontSize: Font.sm,
    lineHeight: LineHeight.sm,
  },
  modalFooter: {
    flexDirection: 'row',
    gap: Gap.xl,
  },
  modalFooterPinned: {
    paddingHorizontal: 14,
    paddingTop: Gap.section,
    paddingBottom: 14,
  },
  modalDangerBtn: {
    flex: 1,
    borderRadius: Radius.md,
    paddingVertical: Gap.xl,
    borderWidth: 1,
    borderColor: Colors.dangerBg,
    backgroundColor: Colors.dangerBg,
    alignItems: 'center',
  },
  modalPrimaryBtn: {
    flex: 1,
    borderRadius: Radius.md,
    paddingVertical: Gap.xl,
    borderWidth: 1,
    borderColor: Colors.chipSelectedBg,
    backgroundColor: Colors.chipSelectedBg,
    alignItems: 'center',
  },
  modalBtnText: {
    color: Colors.textPrimary,
    fontWeight: '800',
  },
  smallBtnPressed: {
    opacity: Opacity.pressed,
  },
  choiceRow: {
    flexDirection: 'row',
    gap: Gap.md,
  },
  choice: {
    flex: 1,
    paddingHorizontal: Gap.xl,
    paddingVertical: Gap.md,
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
    alignItems: 'center',
  },
  choiceSelected: {
    borderColor: Colors.accentBlue,
    backgroundColor: 'rgba(45,130,230,0.18)',
  },
  choiceText: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
    fontWeight: '700',
  },
  choiceTextSelected: {
    color: Colors.textPrimary,
  },
  rowBtn: {
    paddingHorizontal: Gap.xl,
    paddingVertical: Gap.lg,
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
  },
  rowBtnPressed: {
    opacity: Opacity.pressed,
  },
  rowBtnText: {
    color: Colors.textPrimary,
    fontSize: 13,
    fontWeight: '700',
  },
  toggleRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: Gap.lg,
    paddingHorizontal: Gap.xl,
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.borderCard,
    backgroundColor: Colors.backgroundCard,
  },
  toggleLabel: {
    color: Colors.textPrimary,
    fontSize: 13,
    fontWeight: '700',
  },
  toggleValue: {
    color: Colors.textSecondary,
    fontSize: Font.sm,
    opacity: Opacity.pressed,
    fontWeight: '700',
  },
  toggleValueOn: {
    color: Colors.statusGreen,
    opacity: 1,
  },
  orderList: {
    borderRadius: Radius.md,
    borderWidth: 1,
    borderColor: Colors.borderCard,
    overflow: 'hidden',
  },
  orderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: Gap.md,
    paddingHorizontal: Gap.xl,
  },
  orderRowFirst: {
    borderTopLeftRadius: Radius.md,
    borderTopRightRadius: Radius.md,
  },
  orderRowLast: {
    borderBottomLeftRadius: Radius.md,
    borderBottomRightRadius: Radius.md,
  },
  orderRowSeparator: {
    borderTopWidth: 1,
    borderTopColor: Colors.borderSeparator,
  },
  orderRowActive: {
    backgroundColor: Colors.surfaceElevated,
    borderRadius: Radius.md,
    transform: [{scale: 1.03}],
    shadowColor: '#000',
    shadowOffset: {width: 0, height: 4},
    shadowOpacity: 0.3,
    shadowRadius: Gap.md,
    elevation: 8,
  },
  orderName: {
    color: Colors.textPrimary,
    fontSize: 13,
    fontWeight: '700',
  },
  orderBtns: {
    flexDirection: 'row',
    gap: Gap.md,
  },
  orderBtn: {
    width: Size.control,
    height: Size.control,
    borderRadius: Radius.sm,
    borderWidth: 1,
    borderColor: Colors.borderPrimary,
    alignItems: 'center',
    justifyContent: 'center',
  },
  orderBtnText: {
    color: Colors.textPrimary,
    fontSize: Font.lg,
    fontWeight: '900',
  },
});
