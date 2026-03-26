import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import Paginator from '../../../src/components/common/Paginator';

describe('Paginator', () => {
  it('renders prev and next buttons', () => {
    render(<Paginator onPrev={() => {}} onNext={() => {}} />);
    expect(screen.getByLabelText('Previous')).toBeInTheDocument();
    expect(screen.getByLabelText('Next')).toBeInTheDocument();
  });

  it('hides skip buttons when handlers not provided', () => {
    render(<Paginator onPrev={() => {}} onNext={() => {}} />);
    expect(screen.queryByLabelText('Skip to start')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Skip to end')).not.toBeInTheDocument();
  });

  it('shows skip buttons when handlers provided', () => {
    render(<Paginator onSkipPrev={() => {}} onPrev={() => {}} onNext={() => {}} onSkipNext={() => {}} />);
    expect(screen.getByLabelText('Skip to start')).toBeInTheDocument();
    expect(screen.getByLabelText('Skip to end')).toBeInTheDocument();
  });

  it('calls onPrev when prev button clicked', () => {
    const onPrev = vi.fn();
    render(<Paginator onPrev={onPrev} onNext={() => {}} />);
    fireEvent.click(screen.getByLabelText('Previous'));
    expect(onPrev).toHaveBeenCalledOnce();
  });

  it('calls onNext when next button clicked', () => {
    const onNext = vi.fn();
    render(<Paginator onPrev={() => {}} onNext={onNext} />);
    fireEvent.click(screen.getByLabelText('Next'));
    expect(onNext).toHaveBeenCalledOnce();
  });

  it('disables prev buttons when prevDisabled', () => {
    render(<Paginator onPrev={() => {}} onNext={() => {}} prevDisabled />);
    expect(screen.getByLabelText('Previous')).toBeDisabled();
  });

  it('disables next buttons when nextDisabled', () => {
    render(<Paginator onPrev={() => {}} onNext={() => {}} nextDisabled />);
    expect(screen.getByLabelText('Next')).toBeDisabled();
  });

  it('renders children in the center', () => {
    render(
      <Paginator onPrev={() => {}} onNext={() => {}}>
        <span data-testid="center">Page 1</span>
      </Paginator>,
    );
    expect(screen.getByTestId('center')).toHaveTextContent('Page 1');
  });

  it('supports keyboard navigation', () => {
    const onPrev = vi.fn();
    const onNext = vi.fn();
    render(<Paginator onPrev={onPrev} onNext={onNext} keyboard />);
    fireEvent.keyDown(window, { key: 'ArrowLeft' });
    expect(onPrev).toHaveBeenCalledOnce();
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(onNext).toHaveBeenCalledOnce();
  });

  it('does not fire keyboard when disabled', () => {
    const onPrev = vi.fn();
    const onNext = vi.fn();
    render(<Paginator onPrev={onPrev} onNext={onNext} keyboard prevDisabled nextDisabled />);
    fireEvent.keyDown(window, { key: 'ArrowLeft' });
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(onPrev).not.toHaveBeenCalled();
    expect(onNext).not.toHaveBeenCalled();
  });

  it('renders without any buttons when no handlers', () => {
    const { container } = render(<Paginator />);
    expect(container.querySelectorAll('button')).toHaveLength(0);
  });
});

describe('Paginator.Dot', () => {
  it('renders a dot button', () => {
    render(<Paginator.Dot label="Slide 1" />);
    expect(screen.getByLabelText('Slide 1')).toBeInTheDocument();
  });

  it('calls onClick when clicked', () => {
    const onClick = vi.fn();
    render(<Paginator.Dot onClick={onClick} label="Slide 1" />);
    fireEvent.click(screen.getByLabelText('Slide 1'));
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('applies active style to dot', () => {
    const { container } = render(<Paginator.Dot active label="Slide 1" />);
    const btn = container.querySelector('button');
    expect(btn?.style.transform).toContain('scale');
  });
});
