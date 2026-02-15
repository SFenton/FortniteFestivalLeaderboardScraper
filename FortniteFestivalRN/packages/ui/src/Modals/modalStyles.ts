import {StyleSheet} from 'react-native';

export const modalStyles = StyleSheet.create({
  modalCard: {
    padding: 14,
    gap: 12,
    width: '100%',
    maxWidth: 640,
    alignSelf: 'center',
  },
  modalCardMobile: {
    padding: 0,
    borderBottomLeftRadius: 0,
    borderBottomRightRadius: 0,
  },
  modalHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  modalHeaderPinned: {
    paddingHorizontal: 14,
    paddingTop: 14,
    paddingBottom: 24,
  },
  modalScrollContent: {
    flex: 1,
  },
  modalScrollInner: {
    paddingHorizontal: 14,
    gap: 24,
  },
  modalTitle: {
    color: '#FFFFFF',
    fontSize: 22,
    fontWeight: '800',
  },
  modalClose: {
    paddingHorizontal: 10,
    paddingVertical: 8,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: '#2B3B55',
  },
  modalCloseText: {
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
  modalSection: {
    gap: 8,
  },
  modalSectionTitle: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '800',
  },
  modalHint: {
    color: '#D7DEE8',
    opacity: 0.85,
    fontSize: 12,
    lineHeight: 16,
  },
  modalFooter: {
    flexDirection: 'row',
    gap: 12,
  },
  modalFooterPinned: {
    paddingHorizontal: 14,
    paddingTop: 24,
    paddingBottom: 14,
  },
  modalDangerBtn: {
    flex: 1,
    borderRadius: 12,
    paddingVertical: 12,
    borderWidth: 1,
    borderColor: 'rgba(198,40,40,0.4)',
    backgroundColor: 'rgba(198,40,40,0.4)',
    alignItems: 'center',
  },
  modalPrimaryBtn: {
    flex: 1,
    borderRadius: 12,
    paddingVertical: 12,
    borderWidth: 1,
    borderColor: 'rgba(45,130,230,0.4)',
    backgroundColor: 'rgba(45,130,230,0.4)',
    alignItems: 'center',
  },
  modalBtnText: {
    color: '#FFFFFF',
    fontWeight: '800',
  },
  smallBtnPressed: {
    opacity: 0.85,
  },
  choiceRow: {
    flexDirection: 'row',
    gap: 8,
  },
  choice: {
    flex: 1,
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#2B3B55',
    alignItems: 'center',
  },
  choiceSelected: {
    borderColor: '#2D82E6',
    backgroundColor: 'rgba(45,130,230,0.18)',
  },
  choiceText: {
    color: '#D7DEE8',
    fontSize: 12,
    fontWeight: '700',
  },
  choiceTextSelected: {
    color: '#FFFFFF',
  },
  rowBtn: {
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#2B3B55',
  },
  rowBtnPressed: {
    opacity: 0.85,
  },
  rowBtnText: {
    color: '#FFFFFF',
    fontSize: 13,
    fontWeight: '700',
  },
  toggleRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 10,
    paddingHorizontal: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#263244',
    backgroundColor: '#0B1220',
  },
  toggleLabel: {
    color: '#FFFFFF',
    fontSize: 13,
    fontWeight: '700',
  },
  toggleValue: {
    color: '#D7DEE8',
    fontSize: 12,
    opacity: 0.85,
    fontWeight: '700',
  },
  toggleValueOn: {
    color: '#2ecc71',
    opacity: 1,
  },
  orderList: {
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#263244',
    overflow: 'hidden',
  },
  orderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 8,
    paddingHorizontal: 12,
  },
  orderRowFirst: {
    borderTopLeftRadius: 12,
    borderTopRightRadius: 12,
  },
  orderRowLast: {
    borderBottomLeftRadius: 12,
    borderBottomRightRadius: 12,
  },
  orderRowSeparator: {
    borderTopWidth: 1,
    borderTopColor: '#1A2535',
  },
  orderRowActive: {
    backgroundColor: '#1A2940',
    borderRadius: 12,
    transform: [{scale: 1.03}],
    shadowColor: '#000',
    shadowOffset: {width: 0, height: 4},
    shadowOpacity: 0.3,
    shadowRadius: 8,
    elevation: 8,
  },
  orderName: {
    color: '#FFFFFF',
    fontSize: 13,
    fontWeight: '700',
  },
  orderBtns: {
    flexDirection: 'row',
    gap: 8,
  },
  orderBtn: {
    width: 34,
    height: 34,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: '#2B3B55',
    alignItems: 'center',
    justifyContent: 'center',
  },
  orderBtnText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '900',
  },
});
